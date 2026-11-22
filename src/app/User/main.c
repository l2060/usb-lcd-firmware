/* MSU2_LITE custom firmware - M4: MS2-Link protocol (reliable + extensible) */
#include "ch32x035_usbfs_device.h"
#include "debug.h"

static uint16_t lcd_w = 320, lcd_h = 172;
static uint8_t  g_madctl = 0x28u;   /* MADCTL sent by lcd_init_seq */   /* depend on the display orientation */
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
#define FLASH_CS_LOW()   (GPIOB->BCR = GPIO_Pin_7)

#define FW_VER_MAJOR 1
#define FW_VER_MINOR 1
#define MAX_PAYLOAD  2048
#define CAPS         0x03              /* bit0: DISP, bit1: INDEX8 stream */

/* frame: A5 5A | ver | flags | seq | cmd(2) | len(2) | payload | crc16(2) */
#define HDR_LEN 9
#define MAGIC0 0xA5
#define MAGIC1 0x5A

enum { CMD_INFO = 0x0001, CMD_PING = 0x0002, CMD_RESET = 0x0003, CMD_BOOT = 0x00FF,
       CMD_IMG_WRITE = 0x0110, CMD_IMG_FINISH = 0x0111, CMD_MARK = 0x0024,
       CMD_DISP_BEGIN = 0x0100, CMD_DISP_DATA = 0x0101,
       CMD_DISP_END = 0x0102, CMD_DISP_FILL = 0x0103, CMD_DISP_RECT = 0x0104, CMD_DISP_RLE = 0x0105, CMD_DISP_BLIT = 0x0106,
       CMD_STREAM_BEGIN = 0x0120, CMD_STREAM_RECT = 0x0121,
       CMD_STREAM_COMMIT = 0x0122, CMD_STREAM_END = 0x0123, CMD_STREAM_RECT_ACK = 0x0124,
       CMD_FONT_WRITE = 0x0130, CMD_FONT_FINISH = 0x0131, CMD_DISP_TEXT = 0x0132,
       CMD_FONT_DIAG = 0x0134,
       CMD_IMG_READ = 0x0114, CMD_IMG_REDRAW = 0x0115, CMD_IMG_INFO = 0x0112,
       CMD_COLMOD_PROBE = 0x0117,
       CMD_DISPLAY_SET = 0x0140, CMD_DISPLAY_GET = 0x0141,
       CMD_DRAW_RECT = 0x0150, CMD_DRAW_ELLIPSE = 0x0151, CMD_DRAW_LINE = 0x0152 };

enum { ST_OK = 0, ST_CRC = 1, ST_CMD = 2, ST_LEN = 3, ST_BUSY = 4, ST_ARG = 5, ST_FLASH = 6 };
enum { STREAM_FMT_RGB565_RLE = 0, STREAM_FMT_INDEX8_RAW = 1, STREAM_FMT_INDEX8_RLE = 2 };

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

#define FALIAS(a) ((uint32_t)(a) | 0x08000000u)

static void __attribute__((unused)) dbg_mark(uint8_t v)
{
    static uint8_t buf[256] __attribute__((aligned(4)));
    uint32_t i;
    for (i = 0; i < 256; i++) buf[i] = 0xFF;
    buf[0] = v;
    FLASH_ROM_ERASE(FALIAS(0x0000F400u), 256);
    FLASH_ROM_WRITE(FALIAS(0x0000F400u), (uint32_t *)buf, 256);
}

/* ---------------- crc / tx ---------------- */
static void w25_read(uint32_t addr, uint8_t *buf, uint16_t n);
static void w25_erase4k(uint32_t addr);
static void w25_program(uint32_t addr, const uint8_t *d, uint16_t n);
static void draw_bg_from_flash(void);
static void draw_rgb666_probe(void);
static void sys_apply_orientation(void);
static uint8_t sys_rot(void);
static int sys_set_rot(uint8_t rot);

/* ---------------- font package (multi-face) at 0x040000 ---------------- */
#define FONT_ADDR   0x00040000u
#define FONT_END    0x00100000u   /* 768KB font region */

typedef struct {
    uint32_t magic;
    uint8_t  version, faceCount;
    uint16_t reserved;
    uint32_t facesOff, dataOff;
    uint8_t  rsv[16];
} afont_pkg_t;

typedef struct {
    uint8_t  size, flags, lineHeight, baseline;
    uint16_t count;
    uint32_t tableOff, dataOff;
    uint8_t  rsv[8];
} afont_face_t;

typedef struct {
    uint16_t code;
    uint8_t  w, h, advance;
    int8_t   bx, by;
    uint8_t  pad;
    uint32_t off;
} afont_glyph_t;

#define TEXT_DEBUG 0
static uint8_t font_row[64];
static uint8_t font_chunk[512];
#define DT_BAND_BYTES 1024
static uint8_t band_buf[DT_BAND_BYTES];
static uint8_t row_buf[1024];         /* composited band: several scan lines */
#define DT_MAX_GLYPHS 64
enum { DT_GLYPH_BITMAP = 0, DT_GLYPH_MISSING = 1 };
typedef struct { int16_t gx; int16_t gy; uint8_t w, h, kind, pad; uint32_t off; } dt_glyph_t;
static dt_glyph_t dt_list[DT_MAX_GLYPHS];

typedef struct {
    uint8_t fg_r, fg_g, fg_b;
    uint8_t bg_r, bg_g, bg_b;
} dt_color_t;

static int font_face(uint8_t index, afont_pkg_t *pkg, afont_face_t *face)
{
    w25_read(FONT_ADDR, (uint8_t *)pkg, sizeof(*pkg));
    if (pkg->magic != 0x504E4641u || index >= pkg->faceCount) return 0;
    w25_read(FONT_ADDR + pkg->facesOff + (uint32_t)index * sizeof(*face), (uint8_t *)face, sizeof(*face));
    return 1;
}

static int font_glyph(const afont_face_t *face, uint16_t code, afont_glyph_t *out)
{
    uint16_t lo = 0, hi = face->count;
    while (lo < hi) {
        uint16_t mid = (uint16_t)((lo + hi) / 2);
        afont_glyph_t g;
        w25_read(FONT_ADDR + face->tableOff + (uint32_t)mid * sizeof(g), (uint8_t *)&g, sizeof(g));
        if (g.code == code) { *out = g; return 1; }
        if (g.code < code) lo = (uint16_t)(mid + 1); else hi = mid;
    }
    return 0;
}

static uint16_t utf8_next(const uint8_t *t, uint16_t n, uint16_t *i)
{
    uint8_t c0;
    if (*i >= n) return 0;
    c0 = t[(*i)++];
    if (c0 < 0x80u) return c0;
    if ((c0 & 0xE0u) == 0xC0u) {
        if (*i >= n) return 0;
        return (uint16_t)(((c0 & 0x1Fu) << 6) | (t[(*i)++] & 0x3Fu));
    }
    if ((c0 & 0xF0u) == 0xE0u) {
        if (*i + 1u >= n) return 0;
        {
            uint16_t cp = (uint16_t)(((c0 & 0x0Fu) << 12) | ((t[*i] & 0x3Fu) << 6) | (t[*i + 1] & 0x3Fu));
            *i = (uint16_t)(*i + 2);
            return cp;
        }
    }
    return 0;
}

static uint16_t text_missing_advance(const afont_face_t *face)
{
    uint16_t w = (uint16_t)(face->size / 2);
    if (w < 4) w = 4;
    return (uint16_t)(w + 2);
}

static uint8_t text_missing_box_width(const afont_face_t *face)
{
    uint16_t width = (uint16_t)(face->size / 2);
    if (width < 4) width = 4;
    if (width > 24) width = 24;
    return (uint8_t)width;
}

static uint8_t text_missing_box_height(const afont_face_t *face)
{
    uint16_t height = face->size;
    if (height < 6) height = 6;
    if (height > 24) height = 24;
    return (uint8_t)height;
}

static void text_composite_glyph_row(uint16_t cell_x, uint16_t cell_w, int screen_y,
                                     int glyph_x, int glyph_y, uint8_t glyph_w, uint8_t glyph_h,
                                     uint32_t glyph_off, const dt_color_t *colors)
{
    uint16_t glyph_column;
    int cell_right = (int)cell_x + cell_w;
    int glyph_row;

    if (!glyph_w || !glyph_h || screen_y < glyph_y || screen_y >= glyph_y + glyph_h ||
        glyph_x >= cell_right || glyph_x + glyph_w <= (int)cell_x) return;

    glyph_row = screen_y - glyph_y;
    w25_read(FONT_ADDR + glyph_off + (uint32_t)glyph_row * glyph_w, font_chunk, glyph_w);

    for (glyph_column = 0; glyph_column < glyph_w; glyph_column++) {
        uint8_t alpha = font_chunk[glyph_column];
        int screen_x = glyph_x + glyph_column;
        uint16_t pixel_column;
        uint8_t red, green, blue;

        if (!alpha || screen_x < (int)cell_x || screen_x >= cell_right) continue;

        pixel_column = (uint16_t)(screen_x - (int)cell_x);
        red = (uint8_t)((colors->fg_r * alpha + colors->bg_r * (255 - alpha)) / 255);
        green = (uint8_t)((colors->fg_g * alpha + colors->bg_g * (255 - alpha)) / 255);
        blue = (uint8_t)((colors->fg_b * alpha + colors->bg_b * (255 - alpha)) / 255);
        row_buf[pixel_column * 2] = (uint8_t)((red & 0xF8) | (green >> 5));
        row_buf[pixel_column * 2 + 1] = (uint8_t)(((green & 0x1C) << 3) | (blue >> 3));
    }
}

static void text_composite_missing_row(uint16_t cell_x, uint16_t cell_w, int screen_y,
                                       int box_x, int box_y, uint8_t box_w, uint8_t box_h,
                                       const dt_color_t *colors)
{
    uint16_t box_column;
    int cell_right = (int)cell_x + cell_w;

    if (!box_w || !box_h || screen_y < box_y || screen_y >= box_y + box_h ||
        box_x >= cell_right || box_x + box_w <= (int)cell_x) return;

    for (box_column = 0; box_column < box_w; box_column++) {
        int screen_x = box_x + box_column;
        uint16_t pixel_column;

        if (screen_x < (int)cell_x || screen_x >= cell_right) continue;
        if (screen_y != box_y && screen_y != box_y + box_h - 1 &&
            box_column != 0 && box_column != box_w - 1) continue;

        pixel_column = (uint16_t)(screen_x - (int)cell_x);
        row_buf[pixel_column * 2] = (uint8_t)((colors->fg_r & 0xF8) | (colors->fg_g >> 5));
        row_buf[pixel_column * 2 + 1] = (uint8_t)(((colors->fg_g & 0x1C) << 3) | (colors->fg_b >> 3));
    }
}

static void disp_text(uint8_t faceIndex, uint16_t x, uint16_t y, uint16_t w, uint16_t h,
                      uint16_t color, uint16_t bg, const uint8_t *text, uint16_t n)
{
    afont_pkg_t pkg;
    afont_face_t face;
    dt_color_t colors;
    uint16_t text_index, glyph_count, overflow_text_index;
    uint16_t cell_row;
    int pen_x, overflow_pen_x;
    int text_width;
    uint8_t has_overflow;

    if (!font_face(faceIndex, &pkg, &face)) return;

    pen_x = x;
    text_index = 0;
    while (text_index < n) {
        afont_glyph_t glyph;
        uint16_t code = utf8_next(text, n, &text_index);
        if (code == 0) break;
        if (font_glyph(&face, code, &glyph)) pen_x += glyph.advance;
        else pen_x += text_missing_advance(&face);
    }

    text_width = pen_x - (int)x;
    if (w == 0) {
        w = text_width > LCD_W ? LCD_W : (uint16_t)(text_width ? text_width : 1);
    }
    if (h == 0) {
        h = face.lineHeight;
    }
    if (!w || !h) return;
    if ((uint32_t)x >= LCD_W || (uint32_t)y >= LCD_H) return;
    if ((uint32_t)x + w > LCD_W) w = (uint16_t)(LCD_W - x);
    if ((uint32_t)y + h > LCD_H) h = (uint16_t)(LCD_H - y);
    if (!w || !h) return;

    colors.fg_r = (uint8_t)(((color >> 11) & 0x1F) << 3);
    colors.fg_g = (uint8_t)(((color >> 5) & 0x3F) << 2);
    colors.fg_b = (uint8_t)((color & 0x1F) << 3);
    colors.bg_r = (uint8_t)(((bg >> 11) & 0x1F) * 255 / 31);
    colors.bg_g = (uint8_t)(((bg >> 5) & 0x3F) * 255 / 63);
    colors.bg_b = (uint8_t)((bg & 0x1F) * 255 / 31);

    glyph_count = 0;
    has_overflow = 0;
    overflow_text_index = 0;
    overflow_pen_x = 0;
    pen_x = x;
    text_index = 0;
    while (text_index < n) {
        afont_glyph_t glyph;
        uint16_t glyph_text_index = text_index;
        uint16_t code = utf8_next(text, n, &text_index);
        uint16_t glyph_advance;
        uint8_t glyph_w, glyph_h, glyph_kind;
        uint32_t glyph_off;
        int glyph_x, glyph_y;

        if (code == 0) break;
        if (!font_glyph(&face, code, &glyph)) {
            glyph_w = text_missing_box_width(&face);
            glyph_h = text_missing_box_height(&face);
            glyph_advance = text_missing_advance(&face);
            glyph_x = pen_x;
            glyph_y = (int)y + face.baseline - glyph_h;
            glyph_kind = DT_GLYPH_MISSING;
            glyph_off = 0;
        } else {
            glyph_w = glyph.w;
            glyph_h = glyph.h;
            glyph_advance = glyph.advance;
            glyph_x = pen_x + glyph.bx;
            glyph_y = (int)y + face.baseline - glyph.by;
            glyph_kind = DT_GLYPH_BITMAP;
            glyph_off = face.dataOff + glyph.off;
        }

        if (glyph_w && glyph_h && glyph_x < (int)x + w && glyph_x + glyph_w > (int)x &&
            glyph_y < (int)y + h && glyph_y + glyph_h > (int)y) {
            if (glyph_count < DT_MAX_GLYPHS) {
                dt_list[glyph_count].gx = (int16_t)glyph_x;
                dt_list[glyph_count].gy = (int16_t)glyph_y;
                dt_list[glyph_count].w = glyph_w;
                dt_list[glyph_count].h = glyph_h;
                dt_list[glyph_count].kind = glyph_kind;
                dt_list[glyph_count].off = glyph_off;
                glyph_count++;
            } else if (!has_overflow) {
                has_overflow = 1;
                overflow_text_index = glyph_text_index;
                overflow_pen_x = pen_x;
            }
        }
        pen_x += glyph_advance;
    }

    for (cell_row = 0; cell_row < h; cell_row++) {
        uint16_t cell_column;
        uint16_t glyph_index;
        int screen_y = (int)y + cell_row;

        for (cell_column = 0; cell_column < w; cell_column++) {
            row_buf[cell_column * 2] = (uint8_t)(bg >> 8);
            row_buf[cell_column * 2 + 1] = (uint8_t)bg;
        }

        for (glyph_index = 0; glyph_index < glyph_count; glyph_index++) {
            if (dt_list[glyph_index].kind == DT_GLYPH_MISSING) {
                text_composite_missing_row(x, w, screen_y,
                                           dt_list[glyph_index].gx, dt_list[glyph_index].gy,
                                           dt_list[glyph_index].w, dt_list[glyph_index].h, &colors);
            } else {
                text_composite_glyph_row(x, w, screen_y,
                                         dt_list[glyph_index].gx, dt_list[glyph_index].gy,
                                         dt_list[glyph_index].w, dt_list[glyph_index].h,
                                         dt_list[glyph_index].off, &colors);
            }
        }

        if (has_overflow) {
            int overflow_glyph_pen_x = overflow_pen_x;
            uint16_t overflow_index = overflow_text_index;

            while (overflow_index < n) {
                afont_glyph_t glyph;
                uint16_t code = utf8_next(text, n, &overflow_index);

                if (code == 0) break;
                if (!font_glyph(&face, code, &glyph)) {
                    uint8_t box_w = text_missing_box_width(&face);
                    uint8_t box_h = text_missing_box_height(&face);
                    text_composite_missing_row(x, w, screen_y,
                                               overflow_glyph_pen_x,
                                               (int)y + face.baseline - box_h,
                                               box_w, box_h, &colors);
                    overflow_glyph_pen_x += text_missing_advance(&face);
                    continue;
                }

                text_composite_glyph_row(x, w, screen_y,
                                         overflow_glyph_pen_x + glyph.bx,
                                         (int)y + face.baseline - glyph.by,
                                         glyph.w, glyph.h, face.dataOff + glyph.off, &colors);
                overflow_glyph_pen_x += glyph.advance;
            }
        }

        LCD_CS_LOW();
        lcd_set_window(x, (uint16_t)screen_y, w, 1);
        for (cell_column = 0; cell_column < w; cell_column++) {
            lcd_data(row_buf[cell_column * 2]);
            lcd_data(row_buf[cell_column * 2 + 1]);
        }
        spi_flush();
        LCD_CS_HIGH();
    }
}



/* ---------------- drawing primitives ---------------- */
static uint32_t bl_isqrt(uint32_t v)
{
    uint32_t r = 0, b = 1u << 30;
    while (b > v) b >>= 2;
    while (b) {
        if (v >= r + b) { v -= r + b; r = (r >> 1) + b; }
        else r >>= 1;
        b >>= 2;
    }
    return r;
}

/* clipped solid rectangle, single window write */
static void bl_fill(int32_t x, int32_t y, int32_t w, int32_t h, uint16_t c)
{
    int32_t i, n;
    uint8_t bh = (uint8_t)(c >> 8), bl = (uint8_t)(c & 0xFF);

    if (w <= 0 || h <= 0) return;
    if (x < 0) { w += x; x = 0; }
    if (y < 0) { h += y; y = 0; }
    if (x + w > (int32_t)LCD_W) w = (int32_t)LCD_W - x;
    if (y + h > (int32_t)LCD_H) h = (int32_t)LCD_H - y;
    if (w <= 0 || h <= 0) return;

    LCD_CS_LOW();
    lcd_set_window((uint16_t)x, (uint16_t)y, (uint16_t)w, (uint16_t)h);
    n = w * h;
    for (i = 0; i < n; i++) { lcd_data(bh); lcd_data(bl); }
    spi_flush();
    LCD_CS_HIGH();
}

/* ellipse outline restricted to the given quadrants (bit0=+x+y, bit1=-x+y, bit2=-x-y, bit3=+x-y) */
static const int16_t bl_sin_tab[256] = {
         0,    804,   1608,   2410,   3212,   4011,   4808,   5602,
      6393,   7179,   7962,   8739,   9512,  10278,  11039,  11793,
     12539,  13279,  14010,  14732,  15446,  16151,  16846,  17530,
     18204,  18868,  19519,  20159,  20787,  21403,  22005,  22594,
     23170,  23731,  24279,  24811,  25329,  25832,  26319,  26790,
     27245,  27683,  28105,  28510,  28898,  29268,  29621,  29956,
     30273,  30571,  30852,  31113,  31356,  31580,  31785,  31971,
     32137,  32285,  32412,  32521,  32609,  32678,  32728,  32757,
     32767,  32757,  32728,  32678,  32609,  32521,  32412,  32285,
     32137,  31971,  31785,  31580,  31356,  31113,  30852,  30571,
     30273,  29956,  29621,  29268,  28898,  28510,  28105,  27683,
     27245,  26790,  26319,  25832,  25329,  24811,  24279,  23731,
     23170,  22594,  22005,  21403,  20787,  20159,  19519,  18868,
     18204,  17530,  16846,  16151,  15446,  14732,  14010,  13279,
     12539,  11793,  11039,  10278,   9512,   8739,   7962,   7179,
      6393,   5602,   4808,   4011,   3212,   2410,   1608,    804,
         0,   -804,  -1608,  -2410,  -3212,  -4011,  -4808,  -5602,
     -6393,  -7179,  -7962,  -8739,  -9512, -10278, -11039, -11793,
    -12539, -13279, -14010, -14732, -15446, -16151, -16846, -17530,
    -18204, -18868, -19519, -20159, -20787, -21403, -22005, -22594,
    -23170, -23731, -24279, -24811, -25329, -25832, -26319, -26790,
    -27245, -27683, -28105, -28510, -28898, -29268, -29621, -29956,
    -30273, -30571, -30852, -31113, -31356, -31580, -31785, -31971,
    -32137, -32285, -32412, -32521, -32609, -32678, -32728, -32757,
    -32767, -32757, -32728, -32678, -32609, -32521, -32412, -32285,
    -32137, -31971, -31785, -31580, -31356, -31113, -30852, -30571,
    -30273, -29956, -29621, -29268, -28898, -28510, -28105, -27683,
    -27245, -26790, -26319, -25832, -25329, -24811, -24279, -23731,
    -23170, -22594, -22005, -21403, -20787, -20159, -19519, -18868,
    -18204, -17530, -16846, -16151, -15446, -14732, -14010, -13279,
    -12539, -11793, -11039, -10278,  -9512,  -8739,  -7962,  -7179,
     -6393,  -5602,  -4808,  -4011,  -3212,  -2410,  -1608,   -804,
};

static int32_t bl_isin(uint16_t a) { return bl_sin_tab[(a >> 8) & 0xFF]; }

/* ellipse outline by angular sampling: constant step, no polar gaps */
static void bl_ellipse_arc(int32_t x, int32_t y, int32_t w, int32_t h,
                           uint8_t quads, uint8_t thick, uint16_t c)
{
    int32_t cx = x + w / 2, cy = y + h / 2;
    int32_t rx = w / 2, ry = h / 2;
    int32_t n, k, half = thick / 2;
    uint32_t step;
    int32_t mx = (rx > ry) ? rx : ry;

    if (rx < 1 || ry < 1 || !thick) return;

    n = (int32_t)(mx * 7);            /* ~2*pi*mx samples, a bit denser */
    if (n < 48) n = 48;
    if (n > 720) n = 720;
    step = (uint32_t)(65536 / n);

    for (k = 0; k < n; k++) {
        uint16_t a = (uint16_t)(k * step);
        int32_t px = cx + (int32_t)(((int64_t)rx * bl_isin(a)) / 32767);
        int32_t py = cy + (int32_t)(((int64_t)ry * bl_isin((uint16_t)(a + 49152))) / 32767);
        uint8_t q;

        if (px >= cx) q = (py >= cy) ? 0x01 : 0x08;
        else          q = (py >= cy) ? 0x02 : 0x04;
        if (!(quads & q)) continue;

        bl_fill(px - half, py - half, thick, thick, c);
    }
}

static void bl_ellipse_fill(int32_t x, int32_t y, int32_t w, int32_t h, uint16_t c)
{
    int32_t cx = x + w / 2, cy = y + h / 2;
    int32_t rx = w / 2, ry = h / 2, dy;
    if (rx < 1 || ry < 1) return;

    for (dy = -ry; dy <= ry; dy++) {
        uint32_t t = (uint32_t)(ry * ry - dy * dy);
        uint32_t sv, dx;
        if (!t) dx = 0u;                       /* pole: the span is a single pixel */
        else {
            sv = bl_isqrt(t << 16);
            dx = (uint32_t)(((uint64_t)rx * sv) / ((uint64_t)ry * 256u));
        }
        bl_fill(cx - (int32_t)dx, cy + dy, (int32_t)dx * 2 + 1, 1, c);
    }
}

static void bl_rect(int32_t x, int32_t y, int32_t w, int32_t h,
                    uint8_t flags, uint8_t radius, uint8_t thick,
                    uint16_t fill_c, uint16_t border_c)
{
    int32_t i, r;

    if (w <= 0 || h <= 0) return;
    if (!thick) thick = 1;

    r = radius;
    if (r * 2 > w) r = w / 2;
    if (r * 2 > h) r = h / 2;
    if (r < 0) r = 0;

    /* one pass per row: identical inset for fill and border => no gaps */
    for (i = 0; i < h; i++) {
        int32_t inset = 0;
        int32_t x0, x1, len;

        if (r > 0 && (i < r || i >= h - r)) {
            int32_t dy = (i < r) ? (r - 1 - i) : (r - 1 - (h - 1 - i));
            uint32_t t = (uint32_t)(r * r - dy * dy);
            uint32_t sv = bl_isqrt(t << 16);
            inset = r - (int32_t)(((uint64_t)r * sv) / ((uint64_t)r * 256u));
        }

        x0 = x + inset;
        x1 = x + w - 1 - inset;
        len = x1 - x0 + 1;
        if (len <= 0) continue;

        if (flags & 0x01) bl_fill(x0, y + i, len, 1, fill_c);

        if (flags & 0x02) {
            int32_t t = thick;
            if (t > len) t = len;
            if (i < thick || i >= h - thick) {
                /* top / bottom edge spans the full (inset) width */
                bl_fill(x0, y + i, len, 1, border_c);
            } else {
                bl_fill(x0, y + i, t, 1, border_c);
                bl_fill(x1 - t + 1, y + i, t, 1, border_c);
            }
        }
    }
}

static void bl_line(int32_t x0, int32_t y0, int32_t x1, int32_t y1, uint16_t c, uint8_t thick)
{
    int32_t dx, dy, sx, sy, err;
    if (!thick) thick = 1;
    dx = (x1 > x0) ? (x1 - x0) : (x0 - x1);
    dy = (y1 > y0) ? (y0 - y1) : (y1 - y0);
    sx = (x0 < x1) ? 1 : -1;
    sy = (y0 < y1) ? 1 : -1;
    err = dx + dy;

    for (;;) {
        bl_fill(x0 - thick / 2, y0 - thick / 2, thick, thick, c);
        if (x0 == x1 && y0 == y1) break;
        {
            int32_t e2 = err * 2;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}

/* ---------------- display orientation (byte 4/5 of the 0x3000 page) ---------------- */
#define SYS_ADDR   0x00003000u
static uint8_t sys_page[256] __attribute__((aligned(4)));
static const uint8_t madctl_tab[4] = { 0x28u, 0x08u, 0xE8u, 0xC8u };

static uint8_t sys_rot(void)
{
    const uint8_t *p = (const uint8_t *)SYS_ADDR;
    if ((uint8_t)(~p[4]) == p[5] && p[4] <= 3u) return p[4];
    return 0u;
}

static int sys_set_rot(uint8_t rot)
{
    uint16_t i;
    for (i = 0; i < 256u; i++) sys_page[i] = *(const uint8_t *)(SYS_ADDR + i);
    sys_page[4] = rot;
    sys_page[5] = (uint8_t)~rot;
    for (i = 6; i < 256u; i++) { if (sys_page[i] == 0x00u) sys_page[i] = 0xFFu; }

    FLASH->STATR = 0x3Fu;
    if (FLASH_ROM_ERASE(FALIAS(SYS_ADDR), 256) != FLASH_COMPLETE) return 0;
    if (FLASH_ROM_WRITE(FALIAS(SYS_ADDR), (uint32_t *)sys_page, 256) != FLASH_COMPLETE) return 0;
    return sys_rot() == rot;
}

static void sys_apply_orientation(void)
{
    uint8_t rot = sys_rot();
    lcd_w = (rot & 1u) ? 172u : 320u;
    lcd_h = (rot & 1u) ? 320u : 172u;
    LCD_CS_LOW();
    g_madctl = madctl_tab[rot & 3u];
    lcd_cmd(0x36);
    lcd_data(g_madctl);
    LCD_CS_HIGH();
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

/* ---------------- protocol state ---------------- */
static uint8_t  rxbuf[MAX_PAYLOAD];
static uint16_t rxlen;
static uint16_t rxfld;
static uint8_t  rxstate;
static uint32_t rx_idle;
static uint8_t  rxhdr[HDR_LEN];
static uint16_t rxcrc;

static int16_t  dx, dy;
static uint16_t dw, dh;
static uint32_t dleft;      /* remaining pixel bytes */
static uint8_t  dhalf;      /* pending high byte */
static uint8_t  dhalf_ok;   /* 1 = dhalf holds a first byte */
static uint32_t pix_crc;
static uint8_t  blit_open, blit_carry[3], blit_ncarry;
static uint8_t  no_ack;
static uint8_t  st_open, st_carry[8], st_ncarry, st_mode;
static uint32_t st_px, st_total, st_fcrc, st_framepx, st_frames;
static uint32_t blit_px, blit_total;
static void draw_background(void);
static uint32_t img_crc = 0xFFFFFFFFu;
static uint8_t  img_page[256] __attribute__((aligned(4)));
static uint8_t  img_readback[32];
volatile uint8_t cmd_seen;
static uint32_t idle_loops;
#define BG_IDLE_LOOPS  3000000u   /* ~2s of silence -> host is gone */
extern volatile uint8_t g_host_dtr;
static uint32_t rle_px;
static uint8_t  rle_open;
static const uint32_t crc32_nib[16] = {
    0x00000000,0x1DB71064,0x3B6E20C8,0x26D930AC,0x76DC4190,0x6B6B51F4,0x4DB26158,0x5005713C,
    0xEDB88320,0xF00F9344,0xD6D6A3E8,0xCB61B38C,0x9B64C2B0,0x86D3D2D4,0xA00AE278,0xBDBDF21C
};
static inline uint32_t crc32_byte(uint32_t crc, uint8_t b)
{
    crc = (crc >> 4) ^ crc32_nib[(crc ^ (b & 0x0F)) & 0x0F];
    crc = (crc >> 4) ^ crc32_nib[(crc ^ (b >> 4)) & 0x0F];
    return crc;
}

/* Fixed RGB332 palette used by INDEX8 stream frames. */
static uint16_t rgb332_to565(uint8_t idx)
{
    uint8_t r3 = (uint8_t)(idx >> 5);
    uint8_t g3 = (uint8_t)((idx >> 2) & 7u);
    uint8_t b2 = (uint8_t)(idx & 3u);
    uint16_t r5 = (uint16_t)((r3 << 2) | (r3 >> 1));
    uint16_t g6 = (uint16_t)((g3 << 3) | g3);
    uint16_t b5 = (uint16_t)((b2 << 3) | (b2 << 1) | (b2 >> 1));
    return (uint16_t)((r5 << 11) | (g6 << 5) | b5);
}
static uint8_t  dopen;

static uint8_t last_seq;
static uint8_t have_last;

static void disp_begin(int16_t x, int16_t y, uint16_t w, uint16_t h)
{
    dx = x; dy = y; dw = w; dh = h;
    dleft = (uint32_t)w * h * 2;
    dhalf = 0;
    dhalf_ok = 0;
    pix_crc = 0xFFFFFFFF;
    LCD_CS_LOW();
    lcd_set_window((uint16_t)x, (uint16_t)y, w, h);
    dopen = 1;
}

static void disp_data(const uint8_t *p, uint16_t n)
{
    while (n-- && dleft) {
        uint8_t b = *p++;
        pix_crc = crc32_byte(pix_crc, b);
        if (!dhalf_ok) { dhalf = b; dhalf_ok = 1; dleft--; }
        else {
            lcd_data(dhalf);
            lcd_data(b);
            dleft--;
            dhalf_ok = 0;
        }
        if (!dleft) { spi_flush(); LCD_CS_HIGH(); dopen = 0; }
    }
}

static uint8_t handle(uint16_t cmd, const uint8_t *pl, uint16_t len, uint8_t *out, uint16_t *outlen)
{
    *outlen = 0;

    switch (cmd) {
    case CMD_DRAW_RECT:        /* flags(1) x y w h (8) radius(1) thick(1) fill(2) border(2) = 15 */
        if (len < 15) return ST_LEN;
        bl_rect((int16_t)(pl[1] | (pl[2] << 8)), (int16_t)(pl[3] | (pl[4] << 8)),
                (int16_t)(pl[5] | (pl[6] << 8)), (int16_t)(pl[7] | (pl[8] << 8)),
                pl[0], pl[9], pl[10],
                (uint16_t)(pl[11] | (pl[12] << 8)), (uint16_t)(pl[13] | (pl[14] << 8)));
        return ST_OK;

    case CMD_DRAW_ELLIPSE:     /* flags(1) x y w h (8) thick(1) fill(2) border(2) = 14 */
        if (len < 14) return ST_LEN;
        {
            int16_t ex = (int16_t)(pl[1] | (pl[2] << 8));
            int16_t ey = (int16_t)(pl[3] | (pl[4] << 8));
            int16_t ew = (int16_t)(pl[5] | (pl[6] << 8));
            int16_t eh = (int16_t)(pl[7] | (pl[8] << 8));
            uint8_t ef = pl[0], et = pl[9];
            uint16_t efill = (uint16_t)(pl[10] | (pl[11] << 8));
            uint16_t ebrd = (uint16_t)(pl[12] | (pl[13] << 8));
            if (!et) et = 1;
            if (ef & 0x01) bl_ellipse_fill(ex, ey, ew, eh, efill);
            if (ef & 0x02) {
                if (et <= 1) bl_ellipse_arc(ex, ey, ew, eh, 0x0F, 1, ebrd);
                else {
                    int16_t k;
                    for (k = 0; k < et; k++)
                        bl_ellipse_arc(ex + k, ey + k, ew - 2 * k, eh - 2 * k, 0x0F, 1, ebrd);
                }
            }
        }
        return ST_OK;

    case CMD_DRAW_LINE:        /* x0(2) y0(2) x1(2) y1(2) color(2) thick(1) */
        if (len < 11) return ST_LEN;
        bl_line((int16_t)(pl[0] | (pl[1] << 8)), (int16_t)(pl[2] | (pl[3] << 8)),
                (int16_t)(pl[4] | (pl[5] << 8)), (int16_t)(pl[6] | (pl[7] << 8)),
                (uint16_t)(pl[8] | (pl[9] << 8)), pl[10]);
        return ST_OK;

    case CMD_DISPLAY_SET:      /* rot(1) -> store + reboot */
        if (len < 1) return ST_LEN;
        if (pl[0] > 3u) return ST_ARG;
        if (!sys_set_rot(pl[0])) return ST_FLASH;
        return ST_OK;

    case CMD_DISPLAY_GET:
        out[0] = sys_rot();
        out[1] = (uint8_t)(LCD_W & 0xFF);
        out[2] = (uint8_t)(LCD_W >> 8);
        out[3] = (uint8_t)(LCD_H & 0xFF);
        out[4] = (uint8_t)(LCD_H >> 8);
        *outlen = 5;
        return ST_OK;

    case CMD_INFO:
        out[0] = FW_VER_MAJOR; out[1] = FW_VER_MINOR;
        out[2] = LCD_W & 0xFF; out[3] = LCD_W >> 8;
        out[4] = LCD_H & 0xFF; out[5] = LCD_H >> 8;
        out[6] = MAX_PAYLOAD & 0xFF; out[7] = MAX_PAYLOAD >> 8;
        out[8] = CAPS; out[9] = 0; out[10] = 0; out[11] = 0;
        *outlen = 12;
        return ST_OK;

    case CMD_PING:
        if (len > 32) return ST_LEN;
        {
            uint16_t i;
            for (i = 0; i < len; i++) out[i] = pl[i];
        }
        *outlen = len;
        return ST_OK;

    case CMD_RESET:
        NVIC_SystemReset();
        return ST_OK;

    case CMD_MARK:
        {
            uint32_t mk = (uint32_t)(*(volatile uint8_t *)0x0000F400u);
            out[0] = mk & 0xFF; out[1] = (mk >> 8) & 0xFF;
            out[2] = (mk >> 16) & 0xFF; out[3] = (mk >> 24) & 0xFF;
            *outlen = 4;
        }
        return ST_OK;

    case CMD_BOOT:                       /* jump back into the bootloader (no flash write) */
        {
            *(volatile uint32_t *)0x20003D00u = 0x5A5A5A5Au;
            __asm__ volatile("csrc mstatus, %0" :: "r"(0x8));
            NVIC_DisableIRQ(USBFS_IRQn);
            NVIC_ClearPendingIRQ(USBFS_IRQn);
            USBFS_Device_Init(DISABLE, PWR_VDD_SupplyVoltage());
            ((void (*)(void))0x0000u)();
        }
        return ST_OK;

    case CMD_IMG_WRITE:            /* addr(4) + 256B -> external flash page program */
        if (len != 4 + 256) return ST_LEN;
        {
            uint32_t addr = (uint32_t)pl[0] | ((uint32_t)pl[1] << 8) |
                            ((uint32_t)pl[2] << 16) | ((uint32_t)pl[3] << 24);
            uint32_t i;
            if (addr < 0x00020000u || addr >= 0x00040000u || (addr & 0xFF)) return ST_ARG;   /* 128KB slot */
            if (addr == 0x00020000u) img_crc = 0xFFFFFFFFu;

            for (i = 0; i < 256; i++) img_page[i] = pl[4 + i];

            if ((addr & 0xFFFu) == 0) w25_erase4k(addr);   /* erase once per 4K sector */
            w25_program(addr, img_page, 256);

            w25_read(addr, img_readback, 32);
            for (i = 0; i < 32; i++) {
                if (img_readback[i] != img_page[i]) return ST_FLASH;
            }
            for (i = 0; i < 256; i++) img_crc = crc32_byte(img_crc, img_page[i]);
        }
        return ST_OK;

    case CMD_IMG_FINISH:           /* verify accumulated CRC over what was written */
        if (len != 8) return ST_LEN;
        {
            uint32_t crc = (uint32_t)pl[4] | ((uint32_t)pl[5] << 8) |
                           ((uint32_t)pl[6] << 16) | ((uint32_t)pl[7] << 24);
            if ((img_crc ^ 0xFFFFFFFFu) != crc) return ST_FLASH;
            img_crc = 0xFFFFFFFFu;
        }
        return ST_OK;

    case CMD_IMG_INFO:               /* read external flash image header (verify read path) */
        {
            uint8_t h[32];
            uint32_t i;
            w25_read(0x007F0000u, h, 32);
            for (i = 0; i < 32; i++) out[i] = h[i];
            *outlen = 32;
        }
        return ST_OK;

    case CMD_COLMOD_PROBE:               /* COLMOD probe: 3 bands, 3 candidate values */
        draw_rgb666_probe();
        return ST_OK;

    case CMD_IMG_REDRAW:               /* redraw the stored background */
        draw_bg_from_flash();
        return ST_OK;

    case CMD_IMG_READ:               /* addr(4) -> 32 bytes read from external flash */
        if (len != 4) return ST_LEN;
        {
            uint32_t addr = (uint32_t)pl[0] | ((uint32_t)pl[1] << 8) |
                            ((uint32_t)pl[2] << 16) | ((uint32_t)pl[3] << 24);
            uint32_t i;
            w25_read(addr, img_readback, 32);
            for (i = 0; i < 32; i++) out[i] = img_readback[i];
            *outlen = 32;
        }
        return ST_OK;

    case CMD_DISP_BEGIN:
        if (len != 9) return ST_LEN;
        {
            int16_t x = pl[0] | (pl[1] << 8);
            int16_t y = pl[2] | (pl[3] << 8);
            uint16_t w = pl[4] | (pl[5] << 8);
            uint16_t h = pl[6] | (pl[7] << 8);
            if (pl[8] != 0) return ST_ARG;
            if (!w || !h || x < 0 || y < 0 || (x + w) > LCD_W || (y + h) > LCD_H) return ST_ARG;
            disp_begin(x, y, w, h);
        }
        return ST_OK;

    case CMD_DISP_DATA:
        if (!dopen) return ST_BUSY;
        disp_data(pl, len);
        return ST_OK;

    case CMD_FONT_DIAG:        /* report what the device sees in the font package */
        {
            afont_pkg_t pkg;
            afont_face_t face;
            afont_glyph_t g;
            uint32_t i;

            w25_read(FONT_ADDR, (uint8_t *)&pkg, sizeof(pkg));
            out[0] = (uint8_t)(pkg.magic & 0xFF);
            out[1] = (uint8_t)((pkg.magic >> 8) & 0xFF);
            out[2] = (uint8_t)((pkg.magic >> 16) & 0xFF);
            out[3] = (uint8_t)((pkg.magic >> 24) & 0xFF);
            out[4] = pkg.version;
            out[5] = pkg.faceCount;
            out[6] = (uint8_t)sizeof(afont_pkg_t);
            out[7] = (uint8_t)sizeof(afont_face_t);
            out[8] = (uint8_t)sizeof(afont_glyph_t);
            out[9] = (uint8_t)(pkg.facesOff & 0xFF);
            out[10] = (uint8_t)(pkg.facesOff >> 8);

            w25_read(FONT_ADDR + pkg.facesOff, (uint8_t *)&face, sizeof(face));
            out[11] = face.size;
            out[12] = (uint8_t)(face.count & 0xFF);
            out[13] = (uint8_t)(face.count >> 8);
            out[14] = (uint8_t)(face.tableOff & 0xFF);
            out[15] = (uint8_t)((face.tableOff >> 8) & 0xFF);
            out[16] = (uint8_t)((face.tableOff >> 16) & 0xFF);
            out[17] = (uint8_t)(face.tableOff >> 24);
            out[18] = (uint8_t)(face.dataOff & 0xFF);
            out[19] = (uint8_t)((face.dataOff >> 8) & 0xFF);

            w25_read(FONT_ADDR + face.tableOff, (uint8_t *)&g, sizeof(g));
            out[20] = (uint8_t)(g.code & 0xFF);
            out[21] = (uint8_t)(g.code >> 8);
            out[22] = g.w;
            out[23] = g.h;
            *outlen = 24;
            (void)i;
        }
        return ST_OK;

    case CMD_FONT_WRITE:       /* addr(4) + 256 bytes -> font region */
        if (len != 4 + 256) return ST_LEN;
        {
            uint32_t addr = (uint32_t)pl[0] | ((uint32_t)pl[1] << 8) |
                            ((uint32_t)pl[2] << 16) | ((uint32_t)pl[3] << 24);
            uint32_t i;
            if (addr < FONT_ADDR || addr >= FONT_END || (addr & 0xFF)) return ST_ARG;
            for (i = 0; i < 256; i++) img_page[i] = pl[4 + i];
            if ((addr & 0xFFFu) == 0) w25_erase4k(addr);
            w25_program(addr, img_page, 256);
            w25_read(addr, img_readback, 32);
            for (i = 0; i < 32; i++) if (img_readback[i] != img_page[i]) return ST_FLASH;
        }
        return ST_OK;

    case CMD_FONT_FINISH:
        if (len != 8) return ST_LEN;
        {
            afont_pkg_t pkg;
            w25_read(FONT_ADDR, (uint8_t *)&pkg, sizeof(pkg));
            if (pkg.magic != 0x504E4641u) return ST_FLASH;
            out[0] = pkg.faceCount;
            out[1] = pkg.version;
            out[2] = 0;
            *outlen = 3;
        }
        return ST_OK;

    case CMD_DISP_TEXT:        /* face(1) x(2) y(2) w(2) h(2) fg(2) bg(2) + text */
        if (len < 14) return ST_LEN;
        {
            uint8_t  f = pl[0];
            uint16_t x = (uint16_t)(pl[1] | (pl[2] << 8));
            uint16_t y = (uint16_t)(pl[3] | (pl[4] << 8));
            uint16_t w = (uint16_t)(pl[5] | (pl[6] << 8));
            uint16_t h = (uint16_t)(pl[7] | (pl[8] << 8));
            uint16_t c = (uint16_t)(pl[9] | (pl[10] << 8));
            uint16_t b = (uint16_t)(pl[11] | (pl[12] << 8));
            disp_text(f, x, y, w, h, c, b, &pl[13], (uint16_t)(len - 13));
        }
        return ST_OK;

    case CMD_STREAM_BEGIN:
        /* Empty payload preserves the legacy RGB565-RLE protocol. */
        if (len > 1) return ST_LEN;
        st_mode = len ? pl[0] : STREAM_FMT_RGB565_RLE;
        if (st_mode > STREAM_FMT_INDEX8_RLE) return ST_ARG;
        st_fcrc = 0xFFFFFFFFu;
        st_frames = 0;
        st_framepx = 0;
        st_open = 0;
        st_ncarry = 0;
        return ST_OK;

    case CMD_STREAM_RECT:      /* [x,y,w,h,pixels] repeated; no response */
    case CMD_STREAM_RECT_ACK:  /* same payload, response after every packet */
        no_ack = (cmd == CMD_STREAM_RECT);
        {
            uint32_t i;
            uint8_t item_bytes;
            for (i = 0; i < (uint32_t)len; i++) st_fcrc = crc32_byte(st_fcrc, pl[i]);
            i = 0;
            for (;;) {
                if (!st_open) {
                    while (st_ncarry < 8 && i < (uint32_t)len) st_carry[st_ncarry++] = pl[i++];
                    if (st_ncarry < 8) break;
                    {
                        uint16_t x = (uint16_t)(st_carry[0] | (st_carry[1] << 8));
                        uint16_t y = (uint16_t)(st_carry[2] | (st_carry[3] << 8));
                        uint16_t w = (uint16_t)(st_carry[4] | (st_carry[5] << 8));
                        uint16_t h = (uint16_t)(st_carry[6] | (st_carry[7] << 8));
                        st_ncarry = 0;
                        if (w == 0 || h == 0) { no_ack = 0; return ST_ARG; }
                        if ((uint32_t)x + w > LCD_W || (uint32_t)y + h > LCD_H) { no_ack = 0; return ST_ARG; }
                        st_open = 1;
                        st_px = 0;
                        st_total = (uint32_t)w * h;
                        LCD_CS_LOW();
                        lcd_set_window(x, y, w, h);
                    }
                }

                item_bytes = (st_mode == STREAM_FMT_INDEX8_RAW) ? 1u :
                             (st_mode == STREAM_FMT_INDEX8_RLE) ? 2u : 3u;
                while (st_ncarry < item_bytes && i < (uint32_t)len) st_carry[st_ncarry++] = pl[i++];
                if (st_ncarry < item_bytes) break;
                {
                    uint8_t n = (st_mode == STREAM_FMT_INDEX8_RAW) ? 1u : st_carry[0];
                    uint16_t col;
                    if (st_mode == STREAM_FMT_RGB565_RLE)
                        col = (uint16_t)(st_carry[1] | (st_carry[2] << 8));
                    else
                        col = rgb332_to565(st_carry[(st_mode == STREAM_FMT_INDEX8_RAW) ? 0 : 1]);
                    st_ncarry = 0;
                    while (n-- && st_px < st_total) {
                        lcd_data((uint8_t)(col >> 8));
                        lcd_data((uint8_t)(col & 0xFF));
                        st_px++;
                        st_framepx++;
                    }
                }
                if (st_px >= st_total) st_open = 0;
            }
        }
        return ST_OK;

    case CMD_STREAM_COMMIT:    /* crc32(4) over this frame's rect payloads */
        spi_flush();
        LCD_CS_HIGH();
        st_open = 0;
        st_ncarry = 0;
        {
            uint32_t want, got;
            if (len != 4) { st_fcrc = 0xFFFFFFFFu; return ST_LEN; }
            want = (uint32_t)pl[0] | ((uint32_t)pl[1] << 8) | ((uint32_t)pl[2] << 16) | ((uint32_t)pl[3] << 24);
            got = st_fcrc ^ 0xFFFFFFFFu;
            st_fcrc = 0xFFFFFFFFu;
            if (got != want) return ST_CRC;
            st_frames++;
            out[0] = (uint8_t)(st_frames & 0xFF);
            out[1] = (uint8_t)((st_frames >> 8) & 0xFF);
            out[2] = (uint8_t)(st_framepx & 0xFF);
            out[3] = (uint8_t)((st_framepx >> 8) & 0xFF);
            *outlen = 4;
            st_framepx = 0;
        }
        return ST_OK;

    case CMD_STREAM_END:
        spi_flush();
        LCD_CS_HIGH();
        st_open = 0;
        st_ncarry = 0;
        out[0] = (uint8_t)(st_frames & 0xFF);
        out[1] = (uint8_t)((st_frames >> 8) & 0xFF);
        *outlen = 2;
        return ST_OK;

    case CMD_DISP_BLIT:        /* x(2) y(2) w(2) h(2) + [count][lo][hi] runs, streamed */
        {
            uint32_t i = 0;

            if (len == 0) {
                uint32_t c = blit_px;
                spi_flush();
                LCD_CS_HIGH();
                blit_open = 0;
                blit_ncarry = 0;
                out[0] = c & 0xFF; out[1] = (c >> 8) & 0xFF;
                out[2] = (c >> 16) & 0xFF; out[3] = (c >> 24) & 0xFF;
                *outlen = 4;
                return (c == blit_total) ? ST_OK : ST_LEN;
            }

            if (!blit_open) {
                uint16_t x, y, w, h;
                if (len < 8) return ST_LEN;
                x = (uint16_t)(pl[0] | (pl[1] << 8));
                y = (uint16_t)(pl[2] | (pl[3] << 8));
                w = (uint16_t)(pl[4] | (pl[5] << 8));
                h = (uint16_t)(pl[6] | (pl[7] << 8));
                if (w == 0 || h == 0) return ST_ARG;
                if ((uint32_t)x + w > LCD_W || (uint32_t)y + h > LCD_H) return ST_ARG;

                blit_open = 1;
                blit_px = 0;
                blit_total = (uint32_t)w * h;
                blit_ncarry = 0;
                LCD_CS_LOW();
                lcd_set_window(x, y, w, h);
                i = 8;
            }

            for (;;) {
                while (blit_ncarry < 3 && i < (uint32_t)len) blit_carry[blit_ncarry++] = pl[i++];
                if (blit_ncarry < 3) break;
                {
                    uint8_t n = blit_carry[0];
                    uint16_t col = (uint16_t)(blit_carry[1] | (blit_carry[2] << 8));
                    blit_ncarry = 0;
                    while (n-- && blit_px < blit_total) {
                        lcd_data((uint8_t)(col >> 8));
                        lcd_data((uint8_t)(col & 0xFF));
                        blit_px++;
                    }
                }
            }
        }
        return ST_OK;

    case CMD_DISP_RLE:
        {
            uint32_t i = 0;
            if (len == 0) {
                /* end of stream: close window, report pixel count */
                uint32_t c = rle_px;
                spi_flush();
                LCD_CS_HIGH();
                rle_open = 0;
                out[0] = c & 0xFF; out[1] = (c >> 8) & 0xFF;
                out[2] = (c >> 16) & 0xFF; out[3] = (c >> 24) & 0xFF;
                *outlen = 4;
                return (c == (uint32_t)LCD_W * LCD_H) ? ST_OK : ST_LEN;
            }

            if (!rle_open) {
                rle_open = 1;
                rle_px = 0;
                LCD_CS_LOW();
                lcd_set_window(0, 0, LCD_W, LCD_H);
            }

            while (i + 3 <= (uint32_t)len) {
                uint8_t n = pl[i];
                uint16_t col = (uint16_t)(pl[i + 1] | (pl[i + 2] << 8));
                i += 3;
                while (n--) {
                    lcd_data(col >> 8);
                    lcd_data(col & 0xFF);
                    rle_px++;
                }
            }
        }
        return ST_OK;

    case CMD_DISP_END:
        if (dleft) return ST_LEN;
        spi_flush();
        LCD_CS_HIGH();
        dopen = 0;
        {
            uint32_t c = pix_crc ^ 0xFFFFFFFFu;
            out[0] = c & 0xFF; out[1] = (c >> 8) & 0xFF;
            out[2] = (c >> 16) & 0xFF; out[3] = (c >> 24) & 0xFF;
        }
        *outlen = 4;
        return ST_OK;

    case CMD_DISP_FILL:
        if (len != 2) return ST_LEN;
        lcd_fill(pl[0] | (pl[1] << 8));
        return ST_OK;

    case CMD_DISP_RECT:
        if (len != 10) return ST_LEN;
        {
            int16_t x = pl[0] | (pl[1] << 8);
            int16_t y = pl[2] | (pl[3] << 8);
            uint16_t w = pl[4] | (pl[5] << 8);
            uint16_t h = pl[6] | (pl[7] << 8);
            uint16_t c = pl[8] | (pl[9] << 8);
            uint32_t n = (uint32_t)w * h;
            if (!w || !h || x < 0 || y < 0 || (x + w) > LCD_W || (y + h) > LCD_H) return ST_ARG;
            LCD_CS_LOW();
            lcd_set_window((uint16_t)x, (uint16_t)y, w, h);
            while (n--) { lcd_data(c >> 8); lcd_data(c & 0xFF); }
            spi_flush();
            LCD_CS_HIGH();
        }
        return ST_OK;

    default:
        return ST_CMD;
    }
}

static void send_response(uint8_t seq, uint16_t cmd, uint8_t status, const uint8_t *data, uint16_t n)
{
    uint8_t buf[HDR_LEN + 64 + 2];   /* 32-byte payloads need 44 bytes */
    uint16_t i, crc, len = n + 1;

    buf[0] = MAGIC0; buf[1] = MAGIC1; buf[2] = 1; buf[3] = 1; buf[4] = seq;
    buf[5] = cmd & 0xFF; buf[6] = cmd >> 8;
    buf[7] = len & 0xFF; buf[8] = len >> 8;
    buf[9] = status;
    for (i = 0; i < n; i++) buf[10 + i] = data[i];
    crc = crc16(&buf[2], 7 + len);
    buf[9 + len] = crc & 0xFF;
    buf[10 + len] = crc >> 8;
    {
        uint32_t guard = 200000;
        while (USBFS_Endp_DataUp(DEF_UEP3, buf, 9 + len + 2, DEF_UEP_CPY_LOAD) != 0 && --guard)
            ;
    }
}

static void frame_complete(void)
{
    cmd_seen = 1;
    uint8_t out[64];
    uint16_t outlen = 0;
    uint8_t status;
    uint16_t cmd = rxhdr[5] | (rxhdr[6] << 8);
    uint8_t seq = rxhdr[4];

    status = handle(cmd, rxbuf, rxlen, out, &outlen);
    if (no_ack) no_ack = 0;              /* streaming packets are not acknowledged */
    else send_response(seq, cmd, status, out, outlen);
}

static void proto_byte(uint8_t b)
{
    rx_idle = 0;
    switch (rxstate) {
    case 0:
        rxstate = (b == MAGIC0) ? 1 : 0;
        break;
    case 1:
        rxstate = (b == MAGIC1) ? 2 : 0;
        break;
    default:
        if (rxstate < HDR_LEN) {
            rxhdr[rxstate++] = b;
            if (rxstate == HDR_LEN) {
                rxlen = rxhdr[7] | (rxhdr[8] << 8);
                if (rxlen > MAX_PAYLOAD) { rxstate = 0; break; }
                rxfld = 0;
                if (rxlen == 0) rxstate = 100;   /* go to crc */
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
                uint16_t crc = crc16(&rxhdr[2], 7);
                uint16_t i;
                uint16_t c = crc;
                for (i = 0; i < rxlen; i++) {
                    c ^= (uint16_t)rxbuf[i] << 8;
                    {
                        int k;
                        for (k = 0; k < 8; k++)
                            c = (c & 0x8000) ? (uint16_t)((c << 1) ^ 0x1021) : (uint16_t)(c << 1);
                    }
                }
                if (c == rxcrc) frame_complete();
                else send_response(rxhdr[4], rxhdr[5] | (rxhdr[6] << 8), ST_CRC, 0, 0);
            }
        }
        break;
    }
}

/* ---------------- usb ---------------- */
#define RXR_SIZE 2048
static volatile uint16_t rx_head, rx_tail;
static uint8_t rx_ring[RXR_SIZE];

void usb_rx_isr(const uint8_t *p, uint16_t n)
{
    uint16_t i;
    for (i = 0; i < n; i++) {
        uint16_t nh = (uint16_t)((rx_head + 1) & (RXR_SIZE - 1));
        if (nh == rx_tail) return;              /* ring full: drop */
        rx_ring[rx_head] = p[i];
        rx_head = nh;
    }
}

static void usb_rx_poll(void)
{
    while (rx_tail != rx_head) {
        uint8_t b = rx_ring[rx_tail];
        rx_tail = (uint16_t)((rx_tail + 1) & (RXR_SIZE - 1));
        proto_byte(b);
    }
}

/* ---------- background image (palette 3-3-2 + RLE) ---------- */
#define BG_ADDR   0x00009000u

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
    if (h->len == 0 || h->len > (0x0000F000u - (BG_ADDR + sizeof(bg_hdr_t)))) return;

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


/* ---- external W25Q64 access (shares PA6 with LCD DC) ---- */
static void spi_config(uint16_t dir, uint16_t prescaler)
{
    SPI_InitTypeDef sp = {0};
    sp.SPI_Direction = dir;
    sp.SPI_Mode = SPI_Mode_Master;
    sp.SPI_DataSize = SPI_DataSize_8b;
    sp.SPI_CPOL = SPI_CPOL_High;
    sp.SPI_CPHA = SPI_CPHA_2Edge;
    sp.SPI_NSS = SPI_NSS_Soft;
    sp.SPI_BaudRatePrescaler = prescaler;
    sp.SPI_FirstBit = SPI_FirstBit_MSB;
    sp.SPI_CRCPolynomial = 7;
    SPI_Cmd(SPI1, DISABLE);
    SPI_Init(SPI1, &sp);
    SPI_Cmd(SPI1, ENABLE);
}

static void flash_mode(void)
{
    GPIO_InitTypeDef g = {0};
    FLASH_CS_HIGH();
    LCD_CS_HIGH();
    g.GPIO_Pin = GPIO_Pin_6;
    g.GPIO_Mode = GPIO_Mode_IN_FLOATING;
    GPIO_Init(GPIOA, &g);
    spi_config(SPI_Direction_2Lines_FullDuplex, SPI_BaudRatePrescaler_4);
}

static void lcd_mode(void)
{
    GPIO_InitTypeDef g = {0};
    FLASH_CS_HIGH();
    g.GPIO_Pin = GPIO_Pin_6;
    g.GPIO_Mode = GPIO_Mode_Out_PP;
    g.GPIO_Speed = GPIO_Speed_50MHz;
    GPIO_Init(GPIOA, &g);
    GPIO_SetBits(GPIOA, GPIO_Pin_6);
    spi_config(SPI_Direction_1Line_Tx, SPI_BaudRatePrescaler_2);
}

static uint8_t w25_xfer(uint8_t d)
{
    while (!(SPI1->STATR & SPI_I2S_FLAG_TXE)) ;
    SPI1->DATAR = d;
    while (!(SPI1->STATR & SPI_I2S_FLAG_RXNE)) ;
    return (uint8_t)SPI1->DATAR;
}

static void w25_read(uint32_t addr, uint8_t *buf, uint16_t n)
{
    flash_mode();
    FLASH_CS_LOW();
    w25_xfer(0x03);
    w25_xfer((uint8_t)(addr >> 16));
    w25_xfer((uint8_t)(addr >> 8));
    w25_xfer((uint8_t)addr);
    while (n--) *buf++ = w25_xfer(0xFF);
    FLASH_CS_HIGH();
    lcd_mode();
}


static void w25_cmd_addr(uint8_t cmd, uint32_t addr)
{
    FLASH_CS_LOW();
    w25_xfer(cmd);
    w25_xfer((uint8_t)(addr >> 16));
    w25_xfer((uint8_t)(addr >> 8));
    w25_xfer((uint8_t)addr);
}

static void w25_wait(int units)
{
    volatile int i = units;
    while (i-- > 0) ;
}

static void w25_erase4k(uint32_t addr)
{
    flash_mode();
    FLASH_CS_LOW(); w25_xfer(0x06); FLASH_CS_HIGH();      /* WREN */
    w25_cmd_addr(0x20, addr);                              /* sector erase */
    FLASH_CS_HIGH();
    lcd_mode();
    w25_wait(400000);                                      /* ~50ms */
}

static void w25_program(uint32_t addr, const uint8_t *d, uint16_t n)
{
    flash_mode();
    FLASH_CS_LOW(); w25_xfer(0x06); FLASH_CS_HIGH();      /* WREN */
    w25_cmd_addr(0x02, addr);                              /* page program */
    while (n--) w25_xfer(*d++);
    FLASH_CS_HIGH();
    lcd_mode();
    w25_wait(12000);                                       /* ~2ms */
}


static uint8_t  bg_rle[2048];
static uint16_t bg_pal[256];
static uint16_t bg_run_left;
static uint8_t  bg_run_idx;
static uint8_t  bg_lo, bg_hi;

static void bg_pal_init(void)
{
    int i;
    for (i = 0; i < 256; i++) {
        uint8_t r = (uint8_t)(((i >> 5) & 7) * 255 / 7);
        uint8_t g = (uint8_t)(((i >> 2) & 7) * 255 / 7);
        uint8_t b = (uint8_t)((i & 3) * 255 / 3);
        bg_pal[i] = (uint16_t)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    }
}

/* Draws the stored APP background at 0x020000.
   fmt 0x0055 -> [count][lo][hi] RGB565 runs (65536 colours)
   fmt 0x0000 -> [count][index] 3-3-2 palette runs
   Two rows per flash read keeps the RLE buffer sufficient for detailed images. */
static void draw_bg_from_flash(void)
{
    uint8_t hdr[32];
    uint32_t magic, total, off, max_bytes;
    uint16_t fmt;
    int row;

    w25_read(0x00020000u, hdr, 32);
    magic = (uint32_t)hdr[0] | ((uint32_t)hdr[1] << 8) | ((uint32_t)hdr[2] << 16) | ((uint32_t)hdr[3] << 24);
    total = (uint32_t)hdr[12] | ((uint32_t)hdr[13] << 8) | ((uint32_t)hdr[14] << 16) | ((uint32_t)hdr[15] << 24);
    fmt = (uint16_t)(hdr[8] | (hdr[9] << 8));

    if (magic != 0x4732534Du || total == 0 || total > 130000u || hdr[4] != 0x40 || hdr[5] != 0x01) {
        lcd_fill(0xFFFF);
        return;
    }

    bg_pal_init();
    bg_run_left = 0;
    off = 0;

    for (row = 0; row < (int)LCD_H; row += 2) {
        uint16_t rows = (uint16_t)((row + 2 > (int)LCD_H) ? ((int)LCD_H - row) : 2);
        uint16_t buf_len, pos = 0;
        uint32_t need = (uint32_t)rows * 320u, px = 0;

        /* A two-row window can consume at most 3 bytes/pixel (RGB565 RLE)
           or 2 bytes/pixel (palette RLE).  Reading a full 2048-byte buffer
           here used to discard its unconsumed tail and fetch it again on the
           next window. */
        max_bytes = need * ((fmt == 0x0055u) ? 3u : 2u);
        if (max_bytes > sizeof(bg_rle)) max_bytes = sizeof(bg_rle);
        buf_len = (uint16_t)((total - off > max_bytes) ? max_bytes : (total - off));
        if (buf_len < ((fmt == 0x0055u) ? 3u : 2u)) { lcd_fill(0xFFFF); return; }
        w25_read(0x00020000u + 32u + off, bg_rle, buf_len);

        LCD_CS_LOW();
        lcd_set_window(0, (uint16_t)row, LCD_W, rows);
        while (px < need) {
            if (bg_run_left == 0) {
                if (fmt == 0x0055u) {
                    if ((uint32_t)pos + 2u >= (uint32_t)buf_len) break;
                    bg_run_left = bg_rle[pos++];
                    bg_lo = bg_rle[pos++];
                    bg_hi = bg_rle[pos++];
                } else {
                    if ((uint32_t)pos + 1u >= (uint32_t)buf_len) break;
                    bg_run_left = bg_rle[pos++];
                    bg_run_idx = bg_rle[pos++];
                }
            }
            if (fmt == 0x0055u) {
                while (bg_run_left > 0 && px < need) {
                    lcd_data(bg_hi);
                    lcd_data(bg_lo);
                    bg_run_left--;
                    px++;
                }
            } else {
                uint16_t c = bg_pal[bg_run_idx];
                while (bg_run_left > 0 && px < need) {
                    lcd_data((uint8_t)(c >> 8));
                    lcd_data((uint8_t)(c & 0xFF));
                    bg_run_left--;
                    px++;
                }
            }
        }
        while (px < need) {
            lcd_data(0xFF);
            lcd_data(0xFF);
            px++;
        }
        spi_flush();
        LCD_CS_HIGH();
        off += pos;
        if (off >= total) break;
    }
}


static uint8_t host_active;


/* ---- COLMOD probe: same bars drawn with three candidate 18-bit values ---- */
static void lcd_colmod(uint8_t v)
{
    LCD_CS_LOW();
    lcd_cmd(0x3A);
    lcd_data(v);
    LCD_CS_HIGH();
}

static void draw_rgb666_probe(void)
{
    static const uint8_t bars[8][3] = {
        {63, 0, 0}, {0, 63, 0}, {0, 0, 63}, {63, 63, 63},
        {31, 31, 31}, {63, 31, 0}, {0, 63, 63}, {63, 0, 63}
    };
    static const uint8_t modes[3] = {0x66, 0x06, 0x65};
    int m, x, y;

    for (m = 0; m < 3; m++) {
        lcd_colmod(modes[m]);
        LCD_CS_LOW();
        lcd_set_window(0, (uint16_t)(m * 57), LCD_W, 57);
        for (y = 0; y < 57; y++) {
            for (x = 0; x < (int)LCD_W; x++) {
                int b = x / 40;
                lcd_data((uint8_t)(bars[b][0] << 2));
                lcd_data((uint8_t)(bars[b][1] << 2));
                lcd_data((uint8_t)(bars[b][2] << 2));
            }
        }
        spi_flush();
        LCD_CS_HIGH();
    }
    lcd_colmod(0x55);
}

int main(void)
{
    SystemCoreClockUpdate();
    Delay_Init();

    {
        uint8_t r = sys_rot();        /* read before LCD init */
        g_madctl = (r == 1u) ? 0x68u : (r == 2u) ? 0xE8u : (r == 3u) ? 0xA8u : 0x28u;
        lcd_w = (r & 1u) ? 172u : 320u;
        lcd_h = (r & 1u) ? 320u : 172u;
    }
    lcd_gpio_init();
    spi_init();

    if (*(volatile uint32_t *)0x20003D04u == 0x4C43445Fu) {
        /* bootloader already initialised the panel: no reset, no black frame */
        *(volatile uint32_t *)0x20003D04u = 0;
    } else {
        lcd_reset();

        LCD_CS_LOW();
        lcd_init_seq();
        lcd_cmd(0x11);
        delay_loops(600000);
        lcd_cmd(0x29);
        LCD_CS_HIGH();
    }

    draw_bg_from_flash();          /* paints the whole screen: no black clear */          /* stored APP background, white when absent */

    USBFS_RCC_Init();
    USBFS_Device_Init(ENABLE, PWR_VDD_SupplyVoltage());


    sys_apply_orientation();   /* boot hook */
    draw_bg_from_flash();

    for (;;) {
        usb_rx_poll();

        /* restore the background when the host stops talking (port closed / app exited) */
        if (g_host_dtr) {
            host_active = 1;
        } else if (host_active) {
            host_active = 0;
            draw_bg_from_flash();
        }

        if (rxstate) {
            if (++rx_idle > 200000) { rxstate = 0; rx_idle = 0; }
        } else {
            rx_idle = 0;
        }
    }
}
