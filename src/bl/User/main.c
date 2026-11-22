/* MSU2_LITE Bootloader
 * 0x0000 - 0x1FFF  bootloader (8KB)
 * 0x2000 - 0xF6FF  application (54KB)
 * 0xF700 - 0xF7FF  metadata page
 *
 * Protocol: same framing as app (A5 5A | ver | flags | seq | cmd | len | payload | crc16)
 *   0x0001 INFO   -> [0xB0, ver, app_addr(4), app_end(4), page(2)]
 *   0x0010 ERASE  -> erase application area
 *   0x0011 WRITE  -> addr(4 LE) + 256 bytes
 *   0x0012 FINISH -> size(4 LE) + crc32(4 LE)  -> verify, write meta
 *   0x0013 RUN      -> jump to application
 *   0x0004 RESTART  -> ACK, then restart and remain in bootloader
 */
#include "ch32x035_usbfs_device.h"
#include "debug.h"

#define APP_ADDR   0x00009200u   /* 24KB boot image slot below */
#define APP_END    0x0000F700u   /* app last: flag/meta sit below it */
#define META_ADDR  0x00003100u
#define FLAG_ADDR  0x00003000u
#define MARK_PAGE  0x0000F400u
#define BG_ADDR    0x00003200u
#define BG_END     0x00009200u   /* boot image: 0x3200..0x91FF (24KB) */
#define BL_VER     28
#define COUNTDOWN_SECOND_LOOPS  1500000u

#define MAGIC0 0xA5
#define MAGIC1 0x5A
#define HDR_LEN 9
#define MAX_PAYLOAD 2048

enum { CMD_INFO = 0x0001, CMD_RESET = 0x0003, CMD_RESTART = 0x0004, CMD_ERASE = 0x0010, CMD_WRITE = 0x0011,
       CMD_FINISH = 0x0012, CMD_RUN = 0x0013,
       CMD_BG_WRITE = 0x0020, CMD_BG_FINISH = 0x0021, CMD_SELFTEST = 0x0022, CMD_ECHO = 0x0023, CMD_META = 0x0024 ,
       CMD_DISPLAY_SET = 0x0140, CMD_DISPLAY_GET = 0x0141 };

static uint8_t sys_rot(void);
static int bl_set_rot(uint8_t rot);
static void sys_apply_orientation(void);
enum { ST_OK = 0, ST_CRC = 1, ST_CMD = 2, ST_LEN = 3, ST_BUSY = 4, ST_ARG = 5, ST_FLASH = 6 };

typedef struct {
    uint32_t magic;
    uint32_t size;
    uint32_t crc;
    uint32_t reserved;
} meta_t;

/* ---- rx ring ---- */
#define RXR_SIZE 4096
static volatile uint16_t rx_head, rx_tail;
static uint8_t rx_ring[RXR_SIZE];

void usb_rx_isr(const uint8_t *p, uint16_t n)
{
    uint16_t i;
    for (i = 0; i < n; i++) {
        uint16_t nh = (uint16_t)((rx_head + 1) & (RXR_SIZE - 1));
        if (nh == rx_tail) return;
        rx_ring[rx_head] = p[i];
        rx_head = nh;
    }
}

/* ---- crc ---- */
static uint32_t crc32_acc(const uint8_t *p, uint32_t n)
{
    static const uint32_t tab[16] = {
        0x00000000,0x1DB71064,0x3B6E20C8,0x26D930AC,0x76DC4190,0x6B6B51F4,0x4DB26158,0x5005713C,
        0xEDB88320,0xF00F9344,0xD6D6A3E8,0xCB61B38C,0x9B64C2B0,0x86D3D2D4,0xA00AE278,0xBDBDF21C
    };
    uint32_t c = 0xFFFFFFFF;
    while (n--) {
        c = (c >> 4) ^ tab[(c ^ (*p & 0x0F)) & 0x0F];
        c = (c >> 4) ^ tab[(c ^ (*p >> 4)) & 0x0F];
        p++;
    }
    return c ^ 0xFFFFFFFF;
}

static uint16_t crc16(const uint8_t *p, uint32_t n)
{
    uint16_t crc = 0xFFFF;
    while (n--) {
        int i;
        crc ^= (uint16_t)(*p++) << 8;
        for (i = 0; i < 8; i++)
            crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021) : (uint16_t)(crc << 1);
    }
    return crc;
}

/* ---- frame tx ---- */
static void send_response(uint8_t seq, uint16_t cmd, uint8_t status, const uint8_t *data, uint16_t n)
{
    uint8_t buf[96];
    uint16_t i, crc, len = n + 1;

    buf[0] = MAGIC0; buf[1] = MAGIC1; buf[2] = 1; buf[3] = 1; buf[4] = seq;
    buf[5] = cmd & 0xFF; buf[6] = cmd >> 8;
    buf[7] = len & 0xFF; buf[8] = len >> 8;
    buf[9] = status;
    for (i = 0; i < n; i++) buf[10 + i] = data[i];
    crc = crc16(&buf[2], 7 + len);
    buf[9 + len] = crc & 0xFF;
    buf[10 + len] = crc >> 8;
    USBFS_Endp_DataUp(DEF_UEP3, buf, 9 + len + 2, DEF_UEP_CPY_LOAD);
}

/* ---- flash ---- */
#define FALIAS(a)   ((uint32_t)(a) | 0x08000000u)
#define CR_PER      0x00000002u
#define CR_STRT     0x00000040u
#define CR_LOCK     0x00000080u
#define CR_PAGE_PG  0x00010000u
#define CR_PAGE_ER  0x00020000u
#define CR_BUF_LOAD 0x00040000u
#define CR_BUF_RST  0x00080000u
#define SR_BSY      0x00000001u
#define KEY1        0x45670123u
#define KEY2        0xCDEF89ABu

static int f_wait(void)
{
    uint32_t t = 2000000u;
    while ((FLASH->STATR & SR_BSY) && --t) ;
    return t ? 0 : -1;
}

static void f_unlock(void)
{
    FLASH->KEYR = KEY1;
    FLASH->KEYR = KEY2;
    FLASH->MODEKEYR = KEY1;
    FLASH->MODEKEYR = KEY2;
}

static void f_lock(void) { FLASH->CTLR |= CR_LOCK; }

static int f_erase(uint32_t addr)
{
    int r;
    f_unlock();
    FLASH->CTLR |= CR_PER;
    FLASH->ADDR = FALIAS(addr);
    FLASH->CTLR |= CR_STRT;
    r = f_wait();
    FLASH->CTLR &= ~CR_PER;
    f_lock();
    return r;
}

/* CH32X035 page program: 256-byte buffer, erased by the operation itself */
static int f_prog_page(uint32_t addr, const uint8_t *d)
{
    uint32_t i;
    int r = 0;

    f_unlock();
    FLASH->CTLR &= ~(CR_PAGE_ER | 0x20u);
    FLASH->CTLR |= CR_PAGE_PG;
    FLASH->CTLR |= CR_BUF_RST;
    if (f_wait()) r = -1;

    for (i = 0; i < 64 && !r; i++) {
        uint32_t w = (uint32_t)d[i * 4] | ((uint32_t)d[i * 4 + 1] << 8) |
                     ((uint32_t)d[i * 4 + 2] << 16) | ((uint32_t)d[i * 4 + 3] << 24);
        *(volatile uint32_t *)(FALIAS(addr) + i * 4) = w;
        FLASH->CTLR |= CR_BUF_LOAD;
        if (f_wait()) r = -1;
    }

    if (!r) {
        FLASH->ADDR = FALIAS(addr);
        FLASH->CTLR |= CR_STRT;
        if (f_wait()) r = -1;
    }
    FLASH->CTLR &= ~CR_PAGE_PG;
    FLASH->CTLR |= 0x00008000u;   /* FLOCK */
    f_lock();
    return r;
}

static uint8_t page_buf[256] __attribute__((aligned(4)));

static int flash_write_page(uint32_t addr, const uint8_t *data)
{
    uint32_t i;
    if (FLASH_ROM_ERASE(FALIAS(addr), 256) != FLASH_COMPLETE) return -1;
    for (i = 0; i < 256; i++) page_buf[i] = data[i];
    if (FLASH_ROM_WRITE(FALIAS(addr), (uint32_t *)page_buf, 256) != FLASH_COMPLETE) return -2;
    for (i = 0; i < 256; i++) {
        if (*(const volatile uint8_t *)(addr + i) != data[i]) return -3;
    }
    return 0;
}

static int dbg_mark_page(uint8_t v)
{
    uint8_t buf[256];
    uint32_t i;
    for (i = 0; i < 256; i++) buf[i] = 0xFF;
    buf[0] = v;
    return flash_write_page(MARK_PAGE, buf);
}

static int flash_erase_app(void)
{
    uint32_t a;
    for (a = APP_ADDR; a < APP_END; a += 256) {
        if (f_erase(a)) return -1;
    }
    return 0;
}

/* ---- meta ---- */
static uint8_t meta_valid(uint32_t *size, uint32_t *crc)
{
    const meta_t *m = (const meta_t *)META_ADDR;
    if (m->magic != 0x4232534Du) return 0;     /* "MS2B" */
    if (m->size == 0 || m->size > (APP_END - APP_ADDR)) return 0;
    *size = m->size;
    *crc = m->crc;
    return 1;
}

static void meta_write(uint32_t size, uint32_t crc)
{
    uint8_t page[256];
    uint32_t i;
    meta_t *m = (meta_t *)page;
    for (i = 0; i < 256; i++) page[i] = 0xFF;
    m->magic = 0x4232534Du;
    m->size = size;
    m->crc = crc;
    flash_write_page(META_ADDR, page);
}

/* ---- jump ---- */
static void jump_to_app(void)
{
    *(volatile uint32_t *)0x20003D04u = 0x4C43445Fu;   /* LCD already initialised */
    void (*fn)(void);

    /* leave the USB peripheral and interrupt controller in a clean state */
    __asm__ volatile("csrc mstatus, %0" :: "r"(0x8));   /* global interrupts off */
    NVIC_DisableIRQ(USBFS_IRQn);
    NVIC_ClearPendingIRQ(USBFS_IRQn);
    USBFS_Device_Init(DISABLE, PWR_VDD_SupplyVoltage());

    __asm__ volatile("fence.i");
    fn = (void (*)(void))APP_ADDR;      /* app .init starts with "j handle_reset" */
    fn();
}




/* ---- protocol ---- */
static uint8_t  rxbuf[MAX_PAYLOAD];
static uint16_t rxlen, rxfld;
static uint8_t  rxstate;
static uint32_t rx_idle;
static uint8_t  rxhdr[HDR_LEN];
static uint16_t rxcrc;
static volatile uint8_t  stay;
static volatile uint8_t  restart_pending;
static uint32_t wr_crc;
static uint32_t wr_expect;
static uint32_t wr_size;
static uint32_t boot_mark;
static uint32_t bg_crc = 0xFFFFFFFFu;
static uint32_t bg_size;

static uint8_t handle(uint16_t cmd, const uint8_t *pl, uint16_t len, uint8_t *out, uint16_t *outlen)
{
    *outlen = 0;
    switch (cmd) {
    case CMD_INFO:
        out[0] = 0xB0; out[1] = BL_VER;
        out[2] = APP_ADDR & 0xFF; out[3] = (APP_ADDR >> 8) & 0xFF;
        out[4] = (APP_ADDR >> 16) & 0xFF; out[5] = (APP_ADDR >> 24) & 0xFF;
        out[6] = APP_END & 0xFF; out[7] = (APP_END >> 8) & 0xFF;
        out[8] = (APP_END >> 16) & 0xFF; out[9] = (APP_END >> 24) & 0xFF;
        out[10] = 0; out[11] = 1;             /* page size 256 */
        *outlen = 12;
        return ST_OK;

    case CMD_ERASE:
        stay = 1;
        if (flash_erase_app()) return ST_FLASH;
        wr_crc = 0xFFFFFFFFu;
        wr_expect = 0;
        wr_size = 0;
        bg_crc = 0xFFFFFFFFu;
        bg_size = 0;
        return ST_OK;

    case CMD_WRITE:
        if (len != 4 + 256) return ST_LEN;
        {
            uint32_t addr = (uint32_t)pl[0] | ((uint32_t)pl[1] << 8) |
                            ((uint32_t)pl[2] << 16) | ((uint32_t)pl[3] << 24);
            const uint8_t *d = pl + 4;
            uint32_t i;
            if (addr < APP_ADDR || addr >= APP_END || (addr & 0xFF)) return ST_ARG;
            /* accumulate crc over written bytes, in order */
            {
                uint32_t c = wr_crc;
                static const uint32_t tab[16] = {
                    0x00000000,0x1DB71064,0x3B6E20C8,0x26D930AC,0x76DC4190,0x6B6B51F4,0x4DB26158,0x5005713C,
                    0xEDB88320,0xF00F9344,0xD6D6A3E8,0xCB61B38C,0x9B64C2B0,0x86D3D2D4,0xA00AE278,0xBDBDF21C
                };
                for (i = 0; i < 256; i++) {
                    c = (c >> 4) ^ tab[(c ^ (d[i] & 0x0F)) & 0x0F];
                    c = (c >> 4) ^ tab[(c ^ (d[i] >> 4)) & 0x0F];
                }
                wr_crc = c;
            }
            { int w = flash_write_page(addr, d); if (w) { out[0]=(uint8_t)(-w); *outlen=1; return ST_FLASH; } }
            wr_size += 256;
        }
        return ST_OK;

    case CMD_FINISH:
        if (len != 8) return ST_LEN;
        {
            uint32_t size = (uint32_t)pl[0] | ((uint32_t)pl[1] << 8) |
                            ((uint32_t)pl[2] << 16) | ((uint32_t)pl[3] << 24);
            uint32_t crc  = (uint32_t)pl[4] | ((uint32_t)pl[5] << 8) |
                            ((uint32_t)pl[6] << 16) | ((uint32_t)pl[7] << 24);
            uint32_t got = wr_crc ^ 0xFFFFFFFFu;
            if (size > (APP_END - APP_ADDR) || size == 0) return ST_ARG;
            if (got != crc) return ST_FLASH;
            meta_write(size, crc);
            stay = 1;
        }
        return ST_OK;

    case CMD_BG_WRITE:
        if (len != 4 + 256) return ST_LEN;
        {
            uint32_t addr = (uint32_t)pl[0] | ((uint32_t)pl[1] << 8) |
                            ((uint32_t)pl[2] << 16) | ((uint32_t)pl[3] << 24);
            const uint8_t *d = pl + 4;
            uint32_t i;
            static const uint32_t tab[16] = {
                0x00000000,0x1DB71064,0x3B6E20C8,0x26D930AC,0x76DC4190,0x6B6B51F4,0x4DB26158,0x5005713C,
                0xEDB88320,0xF00F9344,0xD6D6A3E8,0xCB61B38C,0x9B64C2B0,0x86D3D2D4,0xA00AE278,0xBDBDF21C
            };
            uint32_t c;
            if (addr < BG_ADDR || addr >= BG_END || (addr & 0xFF)) return ST_ARG;
            if (bg_size == 0) bg_crc = 0xFFFFFFFFu;   /* start of a new upload */
            c = bg_crc;
            for (i = 0; i < 256; i++) {
                c = (c >> 4) ^ tab[(c ^ (d[i] & 0x0F)) & 0x0F];
                c = (c >> 4) ^ tab[(c ^ (d[i] >> 4)) & 0x0F];
            }
            bg_crc = c;
            { int w = flash_write_page(addr, d); if (w) { out[0]=(uint8_t)(-w); *outlen=1; return ST_FLASH; } }
            bg_size += 256;
        }
        return ST_OK;

    case CMD_BG_FINISH:
        if (len != 8) return ST_LEN;
        {
            uint32_t crc = (uint32_t)pl[4] | ((uint32_t)pl[5] << 8) |
                           ((uint32_t)pl[6] << 16) | ((uint32_t)pl[7] << 24);
            if ((bg_crc ^ 0xFFFFFFFFu) != crc) return ST_FLASH;
        }
        return ST_OK;

    case CMD_ECHO:
        {
            uint32_t c = crc32_acc(pl, len);
            out[0] = len & 0xFF; out[1] = (len >> 8) & 0xFF;
            out[2] = c & 0xFF; out[3] = (c >> 8) & 0xFF;
            out[4] = (c >> 16) & 0xFF; out[5] = (c >> 24) & 0xFF;
            *outlen = 6;
        }
        return ST_OK;

    case CMD_DISPLAY_SET:      /* rot(1) */
        if (len < 1) return ST_LEN;
        if (pl[0] > 3u) return ST_ARG;
        if (!bl_set_rot(pl[0])) return ST_FLASH;
        return ST_OK;

    case CMD_DISPLAY_GET:
        out[0] = sys_rot();
        out[1] = (uint8_t)((sys_rot() & 1u) ? 172u : 320u);
        out[2] = 0;
        out[3] = (uint8_t)((sys_rot() & 1u) ? 320u : 172u);
        out[4] = 1;
        *outlen = 5;
        return ST_OK;


    case CMD_META:
        {
            const uint32_t *m  = (const uint32_t *)META_ADDR;
            const uint8_t  *fl = (const uint8_t  *)FLAG_ADDR;
            const uint32_t *w0 = (const uint32_t *)APP_ADDR;
            const uint32_t *bg = (const uint32_t *)BG_ADDR;
            uint32_t v[11];
            uint32_t k;

            v[0] = m[0]; v[1] = m[1]; v[2] = m[2];
            v[3] = (uint32_t)(*fl);
            v[4] = *w0;
            v[5] = bg[0];                                  /* bg magic */
            v[6] = (bg[1] & 0xFFFF) | ((bg[1] >> 16) << 16); /* w,h packed as-is */
            v[7] = (bg[2] & 0xFFFF);                       /* fmt */
            v[8] = bg[3];                                  /* len */
            v[9] = bg[4];                                  /* crc */
            v[10] = (uint32_t)(*(volatile uint8_t *)MARK_PAGE);
            for (k = 0; k < 11; k++) {
                out[k*4+0] = v[k] & 0xFF; out[k*4+1] = (v[k] >> 8) & 0xFF;
                out[k*4+2] = (v[k] >> 16) & 0xFF; out[k*4+3] = (v[k] >> 24) & 0xFF;
            }
            *outlen = 44;
        }
        return ST_OK;

    case CMD_SELFTEST:
        {
            uint32_t i;
            uint8_t pat[256];
            uint32_t wbuf[64];
            volatile uint8_t *rdA = (volatile uint8_t *)0x0000F700u;
            volatile uint8_t *rdB = (volatile uint8_t *)0x0000F700u;

            for (i = 0; i < 256; i++) pat[i] = (uint8_t)(i ^ 0x5A);
            for (i = 0; i < 64; i++)
                wbuf[i] = (uint32_t)pat[i*4] | ((uint32_t)pat[i*4+1] << 8) |
                          ((uint32_t)pat[i*4+2] << 16) | ((uint32_t)pat[i*4+3] << 24);

            /* A: 0xF400 */
            FLASH->STATR = 0x3Fu;
            out[0] = (uint8_t)FLASH_ROM_ERASE(FALIAS(0x0000F700u), 256);
            FLASH_ROM_WRITE(FALIAS(0x0000F700u), wbuf, 256);
            out[1] = (uint8_t)(FLASH->STATR & 0xFF);
            out[2] = rdA[0]; out[3] = rdA[1]; out[4] = rdA[2]; out[5] = rdA[3];

            /* B: 0x9000 */
            FLASH->STATR = 0x3Fu;
            out[6] = 0;   /* read-only second check on the same page */
            /* no second write: the scratch page is shared */
            out[7] = (uint8_t)(FLASH->STATR & 0xFF);
            out[8] = rdB[0]; out[9] = rdB[1]; out[10] = rdB[2]; out[11] = rdB[3];
            *outlen = 12;
        }
        return ST_OK;

    case CMD_RUN:
        jump_to_app();
        return ST_OK;

    case CMD_RESET:       /* compatibility alias for pre-RESTART hosts */
    case CMD_RESTART:
        if (len != 0) return ST_LEN;
        /* frame_complete sends ST_OK before the main loop performs the reset */
        restart_pending = 1;
        return ST_OK;

    default:
        return ST_CMD;
    }
}

static void frame_complete(void)
{
    uint8_t out[64];   /* must cover the largest response (META = 44) */
    uint16_t outlen = 0;
    uint8_t status;
    uint16_t cmd = rxhdr[5] | (rxhdr[6] << 8);
    uint8_t seq = rxhdr[4];

    stay = 1;
    status = handle(cmd, rxbuf, rxlen, out, &outlen);
    send_response(seq, cmd, status, out, outlen);
}

static void proto_byte(uint8_t b)
{
    rx_idle = 0;
    switch (rxstate) {
    case 0: rxstate = (b == MAGIC0) ? 1 : 0; break;
    case 1: rxstate = (b == MAGIC1) ? 2 : 0; break;
    default:
        if (rxstate < HDR_LEN) {
            rxhdr[rxstate++] = b;
            if (rxstate == HDR_LEN) {
                rxlen = rxhdr[7] | (rxhdr[8] << 8);
                if (rxlen > MAX_PAYLOAD) { rxstate = 0; break; }
                rxfld = 0;
                if (rxlen == 0) rxstate = 100;
            }
        } else if (rxstate < 100) {
            rxbuf[rxfld++] = b;
            if (rxfld >= rxlen) rxstate = 100;
        } else if (rxstate == 100) {
            rxcrc = b; rxstate = 101;
        } else {
            rxcrc |= (uint16_t)b << 8;
            rxstate = 0;
            {
                uint16_t c = crc16(&rxhdr[2], 7);
                uint16_t i;
                for (i = 0; i < rxlen; i++) {
                    int k;
                    c ^= (uint16_t)rxbuf[i] << 8;
                    for (k = 0; k < 8; k++)
                        c = (c & 0x8000) ? (uint16_t)((c << 1) ^ 0x1021) : (uint16_t)(c << 1);
                }
                if (c == rxcrc) frame_complete();
            }
        }
        break;
    }
}

/* A disconnected host can leave a truncated frame in the parser.  Never let
 * that partial frame consume all future commands indefinitely. */
static void proto_idle_tick(void)
{
    if (rxstate) {
        if (++rx_idle > 200000u) {
            rxstate = 0;
            rxfld = 0;
            rx_idle = 0;
        }
    } else {
        rx_idle = 0;
    }
}

static void restart_bootloader(void)
{
    volatile uint32_t wait;

    restart_pending = 0;
    /* Retain this only through the reset; main() consumes it and enters BL. */
    *(volatile uint32_t *)0x20003D00u = 0x5A5A5A5Au;
    /* Give the just-queued RESTART ACK time to leave USB before reset. */
    for (wait = 0; wait < 960000u; wait++) { }
    NVIC_SystemReset();
}

/* ===== boot splash: LCD + background from internal flash ===== */
static uint16_t lcd_w = 320, lcd_h = 172;
static uint8_t  g_madctl = 0x28u;   /* MADCTL sent by lcd_init_seq */   /* filled from the stored rotation */
#define LCD_W lcd_w
#define LCD_H lcd_h
#define LCD_Y0 34

#define LCD_CS_LOW()    (GPIOC->BCR  = GPIO_Pin_3)
#define LCD_CS_HIGH()   (GPIOC->BSHR = GPIO_Pin_3)
#define LCD_RST_LOW()   (GPIOA->BCR  = GPIO_Pin_0)
#define LCD_RST_HIGH()  (GPIOA->BSHR = GPIO_Pin_0)
#define LCD_DC_CMD()    (GPIOA->BCR  = GPIO_Pin_6)
#define LCD_DC_DATA()   (GPIOA->BSHR = GPIO_Pin_6)
#define FLASH_CS_HIGH() (GPIOB->BSHR = GPIO_Pin_7)


static void delay_loops(volatile unsigned int n)
{
    while (n--) __asm__ volatile("nop");
}

/* ---------------- LCD ---------------- */
static void busy_wait(volatile uint32_t n)
{
    while (n--) __asm__ volatile("nop");
}

static void lcd_gpio_init(void)
{
    GPIO_InitTypeDef g;

    RCC_APB2PeriphClockCmd(RCC_APB2Periph_GPIOA | RCC_APB2Periph_GPIOB |
                           RCC_APB2Periph_GPIOC | RCC_APB2Periph_SPI1, ENABLE);

    g.GPIO_Speed = GPIO_Speed_50MHz;
    g.GPIO_Mode  = GPIO_Mode_AF_PP;
    g.GPIO_Pin   = GPIO_Pin_5 | GPIO_Pin_7;
    GPIO_Init(GPIOA, &g);

    g.GPIO_Mode = GPIO_Mode_Out_PP;
    g.GPIO_Pin  = GPIO_Pin_0 | GPIO_Pin_6;
    GPIO_Init(GPIOA, &g);

    g.GPIO_Pin = GPIO_Pin_3;
    GPIO_Init(GPIOC, &g);

    g.GPIO_Pin = GPIO_Pin_7;
    GPIO_Init(GPIOB, &g);

    GPIO_SetBits(GPIOC, GPIO_Pin_3);
    GPIO_SetBits(GPIOA, GPIO_Pin_0);
    GPIO_SetBits(GPIOB, GPIO_Pin_7);
}

static void spi_init(void)
{
    SPI_InitTypeDef s;

    SPI_I2S_DeInit(SPI1);
    s.SPI_Direction = SPI_Direction_1Line_Tx;
    s.SPI_Mode = SPI_Mode_Master;
    s.SPI_DataSize = SPI_DataSize_8b;
    s.SPI_CPOL = SPI_CPOL_Low;
    s.SPI_CPHA = SPI_CPHA_1Edge;
    s.SPI_NSS = SPI_NSS_Soft;
    s.SPI_BaudRatePrescaler = SPI_BaudRatePrescaler_2;
    s.SPI_FirstBit = SPI_FirstBit_MSB;
    s.SPI_CRCPolynomial = 7;
    SPI_Init(SPI1, &s);
    SPI_Cmd(SPI1, ENABLE);
}

static inline void spi_byte(unsigned char b)
{
    while (!(SPI1->STATR & SPI_I2S_FLAG_TXE)) ;
    SPI1->DATAR = b;
}

static inline void spi_flush(void)
{
    while (SPI1->STATR & SPI_I2S_FLAG_BSY) ;
}

static void lcd_cmd(unsigned char c)
{
    LCD_DC_CMD();
    spi_byte(c);
    spi_flush();
}

static void lcd_data(unsigned char d)
{
    LCD_DC_DATA();
    spi_byte(d);
}

static void lcd_init_seq(void)
{
    static const unsigned char init[] = {
        0xFE, 0x00,
        0xEF, 0x00,
        0x36, 0x01, 0x28,
        0x3A, 0x01, 0x55,
        0x86, 0x01, 0x98,
        0x89, 0x01, 0x13,
        0x8B, 0x01, 0x80,
        0x8D, 0x01, 0x33,
        0x8E, 0x01, 0x0F,
        0xE8, 0x02, 0x12, 0x00,
        0xEC, 0x03, 0x13, 0x02, 0x88,
        0xFF, 0x01, 0x62,
        0x99, 0x01, 0x3E,
        0x9D, 0x01, 0x4B,
        0x98, 0x01, 0x3E,
        0x9C, 0x01, 0x4B,
        0xC3, 0x01, 0x27,
        0xC4, 0x01, 0x18,
        0xC9, 0x01, 0x0A,
        0xF0, 0x06, 0x47, 0x0C, 0x0A, 0x09, 0x15, 0x33,
        0xF1, 0x06, 0x4B, 0x8F, 0x8F, 0x3B, 0x3F, 0x6F,
        0xF2, 0x06, 0x47, 0x0C, 0x0A, 0x09, 0x15, 0x33,
        0xF3, 0x06, 0x4B, 0x8F, 0x8F, 0x3B, 0x3F, 0x6F,
    };
    unsigned int i = 0;

    while (i < sizeof(init)) {
        unsigned char n, cmd_saved;
        cmd_saved = init[i];
        lcd_cmd(init[i]);
        n = init[i + 1];
        i += 2;
        if ((cmd_saved == 0x36u) && (n == 1u)) {
            lcd_data(g_madctl);           /* orientation from the stored config */
            i += 1;
        } else {
            while (n--) lcd_data(init[i++]);
        }
        spi_flush();
    }
}


/* display orientation is stored in byte 4/5 of the system page at 0x3000 */
#define SYS_ADDR 0x00003000u
static const uint8_t bl_madctl_tab[4] = { 0x28u, 0x08u, 0xE8u, 0xC8u };

static uint8_t sys_rot(void)
{
    const uint8_t *p = (const uint8_t *)SYS_ADDR;
    if ((uint8_t)(~p[4]) == p[5] && p[4] <= 3u) return p[4];
    return 0u;
}


static int bl_set_rot(uint8_t rot)
{
    uint8_t page[256];
    uint16_t i;
    for (i = 0; i < 256u; i++) page[i] = *(const uint8_t *)(SYS_ADDR + i);
    page[4] = rot;
    page[5] = (uint8_t)~rot;
    FLASH->STATR = 0x3Fu;
    if (FLASH_ROM_ERASE(FALIAS(SYS_ADDR), 256) != FLASH_COMPLETE) return 0;
    if (FLASH_ROM_WRITE(FALIAS(SYS_ADDR), (uint32_t *)page, 256) != FLASH_COMPLETE) return 0;
    return sys_rot() == rot;
}

static void sys_apply_orientation(void)
{
    uint8_t rot = sys_rot();
    lcd_w = (rot & 1u) ? 172u : 320u;
    lcd_h = (rot & 1u) ? 320u : 172u;
    LCD_CS_LOW();
    g_madctl = bl_madctl_tab[rot & 3u];
    lcd_cmd(0x36);
    lcd_data(g_madctl);
    LCD_CS_HIGH();
}

static void lcd_reset(void)
{
    LCD_RST_LOW();
    delay_loops(200000);
    LCD_RST_HIGH();
    delay_loops(600000);
}

static void lcd_set_window(uint16_t x, uint16_t y, uint16_t w, uint16_t h)
{
    uint16_t xs = x, xe = x + w - 1;
    uint16_t ys = y + LCD_Y0, ye = y + h - 1 + LCD_Y0;

    lcd_cmd(0x2A);
    lcd_data(xs >> 8); lcd_data(xs & 0xFF);
    lcd_data(xe >> 8); lcd_data(xe & 0xFF);
    lcd_cmd(0x2B);
    lcd_data(ys >> 8); lcd_data(ys & 0xFF);
    lcd_data(ye >> 8); lcd_data(ye & 0xFF);
    lcd_cmd(0x2C);
}

static void lcd_fill(uint16_t color)
{
    uint32_t n = (uint32_t)LCD_W * LCD_H;
    LCD_CS_LOW();
    lcd_set_window(0, 0, LCD_W, LCD_H);
    while (n--) { lcd_data(color >> 8); lcd_data(color & 0xFF); }
    spi_flush();
    LCD_CS_HIGH();
}


/* ---------- background image (palette 3-3-2 + RLE) ---------- */
#define BG_MAGIC  0x4732534Du

typedef struct {
    uint32_t magic;
    uint16_t w, h;
    uint16_t fmt;
    uint16_t pad0;
    uint32_t len;
    uint32_t crc;
    uint8_t  rsv[12];
} bg_hdr_t;   /* 32 bytes, data starts at +32 */

static uint16_t pal332[256];
static void pal_init(void)
{
    int i;
    for (i = 0; i < 256; i++) {
        uint8_t r = (uint8_t)(((i >> 5) & 7) * 255 / 7);
        uint8_t g = (uint8_t)(((i >> 2) & 7) * 255 / 7);
        uint8_t b = (uint8_t)((i & 3) * 255 / 3);
        pal332[i] = (uint16_t)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    }
}

static void draw_background(void)
{
    const bg_hdr_t *h = (const bg_hdr_t *)BG_ADDR;
    const uint8_t *p;
    uint32_t n, i;

    if (h->magic != BG_MAGIC) return;
    if (h->w != LCD_W || h->h != LCD_H) return;
    if (h->len == 0 || h->len > (uint32_t)(BG_END - (BG_ADDR + sizeof(bg_hdr_t)))) return;

    pal_init();
    p = (const uint8_t *)(BG_ADDR + sizeof(bg_hdr_t));
    n = h->len;

    LCD_CS_LOW();
    lcd_set_window(0, 0, LCD_W, LCD_H);
    i = 0;
    while (i + 1 < n) {
        uint8_t run = p[i];
        uint8_t idx = p[i + 1];
        uint16_t c = pal332[idx];
        i += 2;
        while (run--) {
            lcd_data(c >> 8);
            lcd_data(c & 0xFF);
        }
    }
    spi_flush();
    LCD_CS_HIGH();
}



/* ---- tiny 3x5 font: digits 0-9 and 'v' (index 10) ---- */
static const uint8_t g3x5[11][5] = {
    {7,5,5,5,7}, {2,6,2,2,7}, {7,1,7,4,7}, {7,1,7,1,7}, {5,5,7,1,1},
    {7,4,7,1,7}, {7,4,7,5,7}, {7,1,1,1,1}, {7,5,7,5,7}, {7,5,7,1,7},
    {5,5,5,5,2}
};

static void draw_version(uint8_t ver, uint16_t x0, uint16_t y0, uint8_t scale)
{
    uint16_t fg = 0x3E5F;   /* accent cyan */
    uint16_t bg = 0x0862;   /* background */
    uint8_t gl[3];
    int n = 0, gi, ry, sy, cx, sx;
    int w, h;

    gl[n++] = 10;                       /* 'v' */
    if (ver >= 10) gl[n++] = (uint8_t)(ver / 10);
    gl[n++] = (uint8_t)(ver % 10);

    w = n * 3 * scale + (n - 1) * scale;
    h = 5 * scale;

    LCD_CS_LOW();
    lcd_set_window(x0, y0, (uint16_t)w, (uint16_t)h);
    for (ry = 0; ry < 5; ry++) {
        for (sy = 0; sy < scale; sy++) {
            for (gi = 0; gi < n; gi++) {
                uint8_t bits = g3x5[gl[gi]][ry];
                for (cx = 0; cx < 3; cx++) {
                    uint16_t c = (bits & (4 >> cx)) ? fg : bg;
                    for (sx = 0; sx < scale; sx++) { lcd_data(c >> 8); lcd_data(c & 0xFF); }
                }
                if (gi != n - 1) {
                    uint16_t c = bg;
                    for (sx = 0; sx < scale; sx++) { lcd_data(c >> 8); lcd_data(c & 0xFF); }
                }
            }
        }
    }
    spi_flush();
    LCD_CS_HIGH();
}


/* ---- boot countdown + status dot ---- */
static void draw_glyphs(const uint8_t *gl, int n, uint16_t x0, uint16_t y0, uint8_t scale)
{
    uint16_t fg = 0x3E5F, bg = 0x0862;
    int gi, ry, sy, cx, sx;
    int w = n * 3 * scale + (n - 1) * scale;
    int h = 5 * scale;

    LCD_CS_LOW();
    lcd_set_window(x0, y0, (uint16_t)w, (uint16_t)h);
    for (ry = 0; ry < 5; ry++) {
        for (sy = 0; sy < scale; sy++) {
            for (gi = 0; gi < n; gi++) {
                uint8_t bits = g3x5[gl[gi]][ry];
                for (cx = 0; cx < 3; cx++) {
                    uint16_t c = (bits & (4 >> cx)) ? fg : bg;
                    for (sx = 0; sx < scale; sx++) { lcd_data(c >> 8); lcd_data(c & 0xFF); }
                }
                if (gi != n - 1) {
                    for (sx = 0; sx < scale; sx++) { lcd_data(bg >> 8); lcd_data(bg & 0xFF); }
                }
            }
        }
    }
    spi_flush();
    LCD_CS_HIGH();
}

static void draw_countdown(int sec)
{
    uint8_t gl[1];
    gl[0] = (uint8_t)sec;
    draw_glyphs(gl, 1, 292, 146, 3);
}

static void draw_status_dot(uint8_t on)
{
    /* the window covers the whole countdown cell so the digit is erased */
    int r = 6, x, y;
    uint16_t wx = 286, wy = 142, ww = 24, wh = 24;
    uint16_t fg = on ? 0x07E0 : 0x4208;
    uint16_t bg = 0x0862;

    LCD_CS_LOW();
    lcd_set_window(wx, wy, ww, wh);
    for (y = 0; y < (int)wh; y++) {
        for (x = 0; x < (int)ww; x++) {
            int dx = (int)(wx + x) - 298;
            int dy = (int)(wy + y) - 154;
            uint16_t c = (dx * dx + dy * dy <= r * r) ? fg : bg;
            lcd_data(c >> 8);
            lcd_data(c & 0xFF);
        }
    }
    spi_flush();
    LCD_CS_HIGH();
}


int main(void)
{
    uint32_t wait;
    uint32_t size, crc;

    SystemCoreClockUpdate();

    /* the app can request the bootloader by leaving a magic in RAM, then jumping to 0 */
    if (*(volatile uint32_t *)0x20003D00u == 0x5A5A5A5Au) {
        *(volatile uint32_t *)0x20003D00u = 0;
        stay = 1;
    }

    {
        uint8_t r = sys_rot();        /* read before LCD init */
        g_madctl = (r == 1u) ? 0x68u : (r == 2u) ? 0xE8u : (r == 3u) ? 0xA8u : 0x28u;
        lcd_w = (r & 1u) ? 172u : 320u;
        lcd_h = (r & 1u) ? 320u : 172u;
    }
    lcd_gpio_init();
    spi_init();
    lcd_reset();
    LCD_CS_LOW();
    lcd_init_seq();
    lcd_cmd(0x11);
    delay_loops(600000);
    LCD_CS_HIGH();

    lcd_fill(0x0000);          /* black into panel RAM while the display is still off */

    LCD_CS_LOW();
    lcd_cmd(0x29);             /* display on: the first visible frame is already black */
    LCD_CS_HIGH();
    /* only paint the splash when we are actually staying in the bootloader */
    sys_apply_orientation();   /* before splash */
    draw_background();
    draw_countdown(5);

    USBFS_RCC_Init();
    USBFS_Device_Init(ENABLE, PWR_VDD_SupplyVoltage());

    /* 5..1 second countdown; any valid command keeps us in the bootloader */
    {
        int sec;
        for (sec = 5; sec >= 1 && !stay; sec--) {
            draw_countdown(sec);
            for (wait = 0; wait < 1500000u; wait++) {   /* ~1s at 48MHz */
                while (rx_tail != rx_head) {
                    uint8_t b = rx_ring[rx_tail];
                    rx_tail = (uint16_t)((rx_tail + 1) & (RXR_SIZE - 1));
                    proto_byte(b);
                }
                proto_idle_tick();
                if (restart_pending) restart_bootloader();
                if (stay) break;
            }
        }
    }

    if (stay) {
        draw_background();        /* clears the countdown digit */
        draw_status_dot(1);       /* green circle only: staying in bootloader */
    }

    if (!stay && meta_valid(&size, &crc)) {
        /* quick sanity: jump */
        jump_to_app();
    }

    for (;;) {
        while (rx_tail != rx_head) {
            uint8_t b = rx_ring[rx_tail];
            rx_tail = (uint16_t)((rx_tail + 1) & (RXR_SIZE - 1));
            proto_byte(b);
        }
        proto_idle_tick();
        if (restart_pending) restart_bootloader();
    }
}
