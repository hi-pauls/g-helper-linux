/* Checks ghelperd's report-descriptor walk: a node carrying a keyboard
 * collection must never be handed to the app.
 *   cc -I. -o test-units test-units.c && ./test-units */
#define main ghelperd_main
#include "ghelperd.c"
#undef main

struct fixture
{
    const char *name;
    const unsigned char *data;
    int len;
    int keyboard;
};

/* HID 1.11 Appendix B.1 boot keyboard (input part). */
static const unsigned char boot_keyboard[] = {
    0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7,
    0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0xC0,
};

/* ASUS AURA style: vendor page 0xFF31, report 0x5A, nothing else. */
static const unsigned char vendor_only[] = {
    0x06, 0x31, 0xFF, 0x09, 0x76, 0xA1, 0x01, 0x85, 0x5A, 0x19, 0x00, 0x2A,
    0xFF, 0x00, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x05, 0x81, 0x00, 0xC0,
};

/* Vendor collection followed by a keyboard on the same interface. */
static const unsigned char vendor_then_keyboard[] = {
    0x06, 0x31, 0xFF, 0x09, 0x76, 0xA1, 0x01, 0x85, 0x5A, 0x75, 0x08, 0x95,
    0x05, 0x81, 0x00, 0xC0, 0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x05, 0x07,
    0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0xC0,
};

/* Boot mouse: not typing, may be passed. */
static const unsigned char boot_mouse[] = {
    0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00, 0x05, 0x09,
    0x19, 0x01, 0x29, 0x03, 0x81, 0x02, 0xC0, 0xC0,
};

/* Keypad (Generic Desktop 0x07) counts as typing too. */
static const unsigned char keypad[] = { 0x05, 0x01, 0x09, 0x07, 0xA1, 0x01, 0xC0 };

/* Truncated item must not read past the end. */
static const unsigned char truncated[] = { 0x05, 0x01, 0x0A };

int main(void)
{
    const struct fixture fixtures[] = {
        { "boot keyboard", boot_keyboard, sizeof(boot_keyboard), 1 },
        { "vendor only", vendor_only, sizeof(vendor_only), 0 },
        { "vendor then keyboard", vendor_then_keyboard, sizeof(vendor_then_keyboard), 1 },
        { "boot mouse", boot_mouse, sizeof(boot_mouse), 0 },
        { "keypad", keypad, sizeof(keypad), 1 },
        { "truncated", truncated, sizeof(truncated), 0 },
    };
    int failures = 0;
    for (size_t i = 0; i < sizeof(fixtures) / sizeof(fixtures[0]); i++)
    {
        int got = descriptor_has_keyboard(fixtures[i].data, fixtures[i].len);
        int ok = got == fixtures[i].keyboard;
        printf("%s  %s\n", ok ? "PASS" : "FAIL", fixtures[i].name);
        failures += !ok;
    }

    /* Hotkey filter: typing keys never pass, whatever scan code rides along.
     * KEY_Q is 16 and KEY_D is 32, the same numbers as two WMI scan codes. */
    const struct { const char *name; unsigned short code; int scan; int pass; } keys[] = {
        { "KEY_PROG1 (ROG key)", 148, -1, 1 },
        { "KEY_KBDILLUMUP", 229, -1, 1 },
        { "WMI brightness scan on KEY_BRIGHTNESSDOWN", 224, 16, 1 },
        { "KEY_Q with scan 16", KEY_Q, 16, 0 },
        { "KEY_D with scan 32", KEY_D, 32, 0 },
        { "KEY_LEFTSHIFT", KEY_LEFTSHIFT, -1, 0 },
        { "KEY_A with HID usage scan", KEY_A, 0x70004, 0 },
        { "unmapped high key", KEY_MACRO1, -1, 0 },
    };
    for (size_t i = 0; i < sizeof(keys) / sizeof(keys[0]); i++)
    {
        int ok = is_hotkey(keys[i].code, keys[i].scan) == keys[i].pass;
        printf("%s  hotkey filter: %s\n", ok ? "PASS" : "FAIL", keys[i].name);
        failures += !ok;
    }
    return failures != 0;
}
