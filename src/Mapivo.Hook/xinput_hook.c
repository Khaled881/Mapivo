// Mapivo XInput proxy DLL.
// Sits next to the game exe under the name the game imports (xinput1_3.dll etc).
// Windows loads us instead of the system DLL; we load the real one by full path
// and forward every call, remapping input slot -> physical pad. No device is created.
//
// A slot may point at an XInput pad ("x0".."x3") or at a DirectInput pad
// ("jVVVV_PPPP", matched on the HID vendor/product). DirectInput pads are read
// and converted to an XInput gamepad on the fly, with vibration forwarded back
// through DirectInput force feedback.
//
// mapivo.ini (next to the game exe) carries the per-game mapping:
//   0.src=j2563_0575     slot 0 reads this physical pad
//   0.vib=1              vibration allowed for slot 0
//   0.a=b2               virtual A driven by physical button 2
// Target names: a b x y lb rb back start l3 r3 up down left right lt rt lx ly rx ry
// Source names: same set, plus b0..b31 for raw buttons of a DirectInput pad.

#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

typedef struct { WORD wButtons; BYTE bLeftTrigger, bRightTrigger;
                 SHORT sThumbLX, sThumbLY, sThumbRX, sThumbRY; } PAD_GAMEPAD;

// HID fallback for pads whose rumble is not exposed over DirectInput.
// HIDP_CAPS is declared here instead of pulling in hidpi.h for one struct.
typedef struct {
    USHORT Usage;
    USHORT UsagePage;
    USHORT InputReportByteLength;
    USHORT OutputReportByteLength;
    USHORT FeatureReportByteLength;
    USHORT Reserved[17];
    USHORT NumberLinkCollectionNodes;
    USHORT NumberInputButtonCaps;
    USHORT NumberInputValueCaps;
    USHORT NumberInputDataIndices;
    USHORT NumberOutputButtonCaps;
    USHORT NumberOutputValueCaps;
    USHORT NumberOutputDataIndices;
    USHORT NumberFeatureButtonCaps;
    USHORT NumberFeatureValueCaps;
    USHORT NumberFeatureDataIndices;
} PF_HIDP_CAPS;

typedef BOOLEAN (WINAPI *PFN_HID_SETOUTPUT)(HANDLE, PVOID, ULONG);
typedef BOOLEAN (WINAPI *PFN_HID_PREPARSED)(HANDLE, PVOID *);
typedef LONG (WINAPI *PFN_HIDP_GETCAPS)(PVOID, PF_HIDP_CAPS *);
typedef struct { DWORD dwPacketNumber; PAD_GAMEPAD Gamepad; } PAD_STATE;
typedef struct { WORD wLeftMotorSpeed, wRightMotorSpeed; } PAD_VIBRATION;

typedef DWORD (WINAPI *PFN_GETSTATE)(DWORD, PAD_STATE*);
typedef DWORD (WINAPI *PFN_SETSTATE)(DWORD, PAD_VIBRATION*);
typedef DWORD (WINAPI *PFN_GETCAPS)(DWORD, DWORD, void*);
typedef DWORD (WINAPI *PFN_GENERIC)(DWORD, DWORD, void*);

enum { T_A, T_B, T_X, T_Y, T_LB, T_RB, T_BACK, T_START, T_L3, T_R3,
       T_UP, T_DOWN, T_LEFT, T_RIGHT, T_LT, T_RT,
       T_LU, T_LD, T_LL, T_LR, T_RU, T_RD, T_RL, T_RR, T_COUNT };

static const char *TARGET_NAME[T_COUNT] = {
    "a", "b", "x", "y", "lb", "rb", "back", "start", "l3", "r3",
    "up", "down", "left", "right", "lt", "rt",
    "lu", "ld", "ll", "lr", "ru", "rd", "rl", "rr"
};

// Each stick direction defaults to the matching stick input, so an unmapped
// slot still passes the stick through unchanged.
static const char *DefaultSource(int t)
{
    switch (t)
    {
    case T_LU: return "ly+";
    case T_LD: return "ly-";
    case T_LL: return "lx-";
    case T_LR: return "lx+";
    case T_RU: return "ry+";
    case T_RD: return "ry-";
    case T_RL: return "rx-";
    case T_RR: return "rx+";
    default:   return TARGET_NAME[t];
    }
}

// slot -> XInput physical index (-1 = not an XInput pad); vib[slot] = allow vibration
static int map[4] = { 0, 1, 2, 3 };
static int vib[4] = { 1, 1, 1, 1 };
static char diToken[4][24];          // "" = slot is XInput or empty
static char slotMap[4][T_COUNT][8];  // target -> source name
static int mapped[4];
static int cfgPresent;               // mapivo.ini was found
static unsigned long long autoTick;  // throttle for device auto-detection
static int route[4][16];             // target -> raw HID index on a DInput pad
static int loaded = 0;
static HMODULE g_self;

static void Log(const char *msg);
static void BuildRoute(int slot);

static void InitDefaults(void)
{
    for (int s = 0; s < 4; s++)
    {
        for (int t = 0; t < T_COUNT; t++)
            strcpy(slotMap[s][t], DefaultSource(t));
        for (int b = 0; b < 16; b++) route[s][b] = -1;
    }
}

/* ---------------- DirectInput ---------------- */

static IDirectInput8W *g_di;
static IDirectInputDevice8W *g_dev[4];
static IDirectInputEffect *g_eff[4];
static int g_effTried[4];                // CreateEffect attempts per device
static HANDLE g_hid[4] = { INVALID_HANDLE_VALUE, INVALID_HANDLE_VALUE,
                           INVALID_HANDLE_VALUE, INVALID_HANDLE_VALUE };
static int g_hidLen[4];
static int g_hidTried[4];
static int g_hidFails[4];                // consecutive write failures
static DWORD g_hidRetry[4];              // tick before which we do not reopen
static int g_hidLogged[4];               // last logged motor pair, for diagnostics
static int g_hidLayout[4];               // 1..6 = report shape this pad accepted
static int g_hidBt[4];                   // 1 when the pad is on Bluetooth
static int g_hidL[4];                    // last left motor asked for (0..255)
static int g_hidR[4];                    // last right motor asked for
static int g_hidIdle[4];                 // 1 once the stop report was sent
static LONG g_hidPump;                   // watchdog thread started?
static DWORD g_hidLast[4];               // tick of the last non-zero request
static DWORD g_hidUntil[4];              // rumble window; zero means the stop report is due
static int g_hidCalls[4];                // first few incoming requests are logged
static unsigned long long g_devNextTry[4]; // throttles re-enumeration while unplugged

static int CfgMatch(unsigned vid, unsigned pid, char *out, int cap);
static int IsBluetoothPathA(const char *path);
static int SonyKind(unsigned vid, unsigned pid);
static unsigned SonyCrc32(unsigned crc, const unsigned char *d, int n);
static void SonySeal(unsigned char *rep, int size);
static void SonyWrite(int slot, int kind, int bt, int left, int right);

// Per-device layout discovered from EnumObjects. c_dfDIJoystick2's fixed offsets
// are rejected by pads whose HID report differs, so the format is rebuilt to match.
typedef struct { int lx, ly, rx, ry, lz, lrz, pov; int zStick; int trMin[2], trMax[2];
                 int stMin[4], stMax[4]; int ff;
                 int btn[32]; int btnCount; UINT size; } DILAYOUT;
static DILAYOUT g_lay[4];

static IDirectInput8W *DI(void)
{
    if (g_di) return g_di;
    char sysD[MAX_PATH], dllD[MAX_PATH];
    if (!GetSystemDirectoryA(sysD, MAX_PATH)) return NULL;
    wsprintfA(dllD, "%s\\dinput8.dll", sysD);
    HMODULE m = LoadLibraryExA(dllD, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!m) return NULL;
    typedef HRESULT (WINAPI *PFN_CREATE)(HINSTANCE, DWORD, REFIID, LPVOID*, LPUNKNOWN);
    PFN_CREATE create = (PFN_CREATE)GetProcAddress(m, "DirectInput8Create");
    if (!create) return NULL;
    if (FAILED(create(GetModuleHandleA(NULL), DIRECTINPUT_VERSION, &IID_IDirectInput8W,
                      (LPVOID*)&g_di, NULL))) return NULL;
    return g_di;
}

static void DiTokenOf(const GUID *product, char *out)
{
    // DirectInput packs the vendor in the low word of guidProduct.Data1.
    unsigned vid = product->Data1 & 0xFFFF;
    unsigned pid = (product->Data1 >> 16) & 0xFFFF;
    wsprintfA(out, "j%04X_%04X", vid, pid);
}

static DIDATAFORMAT g_df[4];
static DIOBJECTDATAFORMAT g_obj[4][48];
static int g_objCount[4];

static const GUID *GuidConst(REFGUID g)
{
    if (IsEqualGUID(g, &GUID_XAxis))  return &GUID_XAxis;
    if (IsEqualGUID(g, &GUID_YAxis))  return &GUID_YAxis;
    if (IsEqualGUID(g, &GUID_ZAxis))  return &GUID_ZAxis;
    if (IsEqualGUID(g, &GUID_RxAxis)) return &GUID_RxAxis;
    if (IsEqualGUID(g, &GUID_RyAxis)) return &GUID_RyAxis;
    if (IsEqualGUID(g, &GUID_RzAxis)) return &GUID_RzAxis;
    if (IsEqualGUID(g, &GUID_POV))    return &GUID_POV;
    if (IsEqualGUID(g, &GUID_Button)) return &GUID_Button;
    return &GUID_Unknown;
}

static BOOL CALLBACK LayoutCb(LPCDIDEVICEOBJECTINSTANCEW oi, LPVOID ctx)
{
    int slot = *(int *)ctx;
    DILAYOUT *L = &g_lay[slot];
    DWORD type = oi->dwType;
    DWORD kind = type & 0xFF;

    // Collections carry no data and DirectInput rejects them in a format.
    if ((type & DIDFT_COLLECTION) || (type & 0x80)) return DIENUM_CONTINUE;
    if (!(type & 0x03) && !(type & DIDFT_PSHBUTTON) && !(type & DIDFT_POV))
        return DIENUM_CONTINUE;
    if (g_objCount[slot] >= 48) return DIENUM_CONTINUE;

    const GUID *pg = GuidConst(&oi->guidType);
    DIOBJECTDATAFORMAT *od = &g_obj[slot][g_objCount[slot]++];
    od->pguid = (GUID *)pg;
    od->dwOfs = oi->dwOfs;
    od->dwType = type;
    od->dwFlags = 0;

    int end;
    if (IsEqualGUID(&oi->guidType, &GUID_POV)) {
        L->pov = oi->dwOfs; end = oi->dwOfs + 4;
    } else if (type & 0x03) {
        if (IsEqualGUID(&oi->guidType, &GUID_XAxis))       L->lx = oi->dwOfs;
        else if (IsEqualGUID(&oi->guidType, &GUID_YAxis))  L->ly = oi->dwOfs;
        else if (IsEqualGUID(&oi->guidType, &GUID_ZAxis))  L->lz = oi->dwOfs;
        else if (IsEqualGUID(&oi->guidType, &GUID_RxAxis)) L->rx = oi->dwOfs;
        else if (IsEqualGUID(&oi->guidType, &GUID_RyAxis)) L->ry = oi->dwOfs;
        else if (IsEqualGUID(&oi->guidType, &GUID_RzAxis)) L->lrz = oi->dwOfs;
        end = oi->dwOfs + 4;
    } else {
        if (L->btnCount < 32) L->btn[L->btnCount++] = oi->dwOfs;
        end = oi->dwOfs + 1;
    }
    if ((UINT)end > L->size) L->size = (UINT)end;
    return DIENUM_CONTINUE;
}

typedef struct { const char *token; GUID guid; int found; } ENUM_CTX;

static BOOL CALLBACK EnumCb(LPCDIDEVICEINSTANCEW inst, LPVOID ctx)
{
    ENUM_CTX *e = (ENUM_CTX*)ctx;
    char tok[24];
    DiTokenOf(&inst->guidProduct, tok);
    if (strcmp(tok, e->token) == 0) {
        e->guid = inst->guidInstance;
        e->found = 1;
        return DIENUM_STOP;
    }
    return DIENUM_CONTINUE;
}

static void Trim(char *s)
{
    char *p = s;
    while (*p == ' ' || *p == '\t') p++;
    if (p != s) memmove(s, p, strlen(p) + 1);
    size_t n = strlen(s);
    while (n && (s[n-1] == ' ' || s[n-1] == '\t' || s[n-1] == '\r' || s[n-1] == '\n')) s[--n] = 0;
}

static void AxisRange(IDirectInputDevice8W *dev, int ofs, int *mn, int *mx)
{
    if (ofs < 0) return;
    DIPROPRANGE pr;
    memset(&pr, 0, sizeof pr);
    pr.diph.dwSize = sizeof pr;
    pr.diph.dwHeaderSize = sizeof(DIPROPHEADER);
    pr.diph.dwObj = ofs;
    pr.diph.dwHow = DIPH_BYOFFSET;
    if (SUCCEEDED(dev->lpVtbl->GetProperty(dev, DIPROP_RANGE, &pr.diph))) {
        *mn = pr.lMin;
        *mx = pr.lMax;
    }
}

// Forget a pad so a replugged one is found again. Its force-feedback effect
// belongs to the old device and must go with it, otherwise vibration never
// comes back after the pad is switched off and on.
static void DropDevice(int slot)
{
    if (g_eff[slot]) { g_eff[slot]->lpVtbl->Release(g_eff[slot]); g_eff[slot] = NULL; }
    if (g_hid[slot] != INVALID_HANDLE_VALUE) { CloseHandle(g_hid[slot]); g_hid[slot] = INVALID_HANDLE_VALUE; }
    g_hidUntil[slot] = 0;
    g_hidIdle[slot] = 1;
    g_hidTried[slot] = 0;
    g_hidLayout[slot] = 0;
    g_hidL[slot] = 0;
    g_hidR[slot] = 0;
    g_hidIdle[slot] = 1;
    if (g_dev[slot])
    {
        g_dev[slot]->lpVtbl->Unacquire(g_dev[slot]);
        g_dev[slot]->lpVtbl->Release(g_dev[slot]);
        g_dev[slot] = NULL;
    }
    g_effTried[slot] = 0;
}

static IDirectInputDevice8W *DiDevice(int slot)
{
    if (g_dev[slot]) return g_dev[slot];

    // A pad that was unplugged must be looked for again, but enumeration is
    // expensive, so retry about once a second instead of every frame.
    unsigned long long now = GetTickCount64();
    if (now < g_devNextTry[slot]) return NULL;
    g_devNextTry[slot] = now + 1000;

    if (!diToken[slot][0]) return NULL;
    IDirectInput8W *di = DI();
    if (!di) return NULL;

    ENUM_CTX e;
    memset(&e, 0, sizeof e);
    e.token = diToken[slot];
    if (FAILED(di->lpVtbl->EnumDevices(di, DI8DEVCLASS_GAMECTRL, EnumCb, &e, DIEDFL_ATTACHEDONLY)))
        return NULL;
    if (!e.found) return NULL;

    IDirectInputDevice8W *dev;
    if (FAILED(di->lpVtbl->CreateDevice(di, &e.guid, &dev, NULL))) return NULL;

    DILAYOUT *L = &g_lay[slot];
    memset(L, 0, sizeof *L);
    L->lx = L->ly = L->rx = L->ry = L->lz = L->lrz = L->pov = -1;
    L->trMin[0] = 0; L->trMax[0] = 32767;
    L->trMin[1] = 0; L->trMax[1] = 32767;
    for (int i = 0; i < 4; i++) { L->stMin[i] = -32768; L->stMax[i] = 32767; }
    g_objCount[slot] = 0;
    if (FAILED(dev->lpVtbl->EnumObjects(dev, LayoutCb, &slot, 0)) || !g_objCount[slot]) {
        Log("EnumObjects failed, device unusable");
        dev->lpVtbl->Release(dev);
        return NULL;
    }

    // Pads with only X,Y,Z,Rz (this ShanWan one included) have no trigger axes
    // at all -- their triggers are buttons. Z/Rz therefore carry the right
    // stick, and reading them as triggers is what made R3 look like one.
    if (L->rx < 0 && L->ry < 0 && L->lz >= 0 && L->lrz >= 0) {
        L->rx = L->lz;
        L->ry = L->lrz;
        L->lz = L->lrz = -1;
        L->zStick = 1;
    }

    // dxDataSize must be DWORD-aligned or SetDataFormat returns E_INVALIDARG.
    L->size = (L->size + 3) & ~3u;
    DIDATAFORMAT *df = &g_df[slot];
    memset(df, 0, sizeof *df);
    df->dwSize = sizeof(DIDATAFORMAT);
    df->dwObjSize = sizeof(DIOBJECTDATAFORMAT);
    df->dwFlags = DIDF_ABSAXIS;
    df->dwDataSize = L->size;
    df->dwNumObjs = g_objCount[slot];
    df->rgodf = g_obj[slot];
    if (FAILED(dev->lpVtbl->SetDataFormat(dev, df))) {
        Log("SetDataFormat failed");
        dev->lpVtbl->Release(dev);
        return NULL;
    }

    HWND hwnd = GetActiveWindow();
    if (hwnd) dev->lpVtbl->SetCooperativeLevel(dev, hwnd, DISCL_BACKGROUND | DISCL_NONEXCLUSIVE);
    dev->lpVtbl->Acquire(dev);

    // DIPROP_RANGE only answers once the device is set up and acquired: it
    // fails with E_NOTIMPL before SetDataFormat. Move the query here.
    AxisRange(dev, L->lz, &L->trMin[0], &L->trMax[0]);
    AxisRange(dev, L->lrz, &L->trMin[1], &L->trMax[1]);
    AxisRange(dev, L->lx, &L->stMin[0], &L->stMax[0]);
    AxisRange(dev, L->ly, &L->stMin[1], &L->stMax[1]);
    AxisRange(dev, L->rx, &L->stMin[2], &L->stMax[2]);
    AxisRange(dev, L->ry, &L->stMin[3], &L->stMax[3]);

    // A pad only buzzes if DirectInput reports a rumble actuator, so report
    // that instead of assuming every pad can.
    {
        DIDEVCAPS caps;
        memset(&caps, 0, sizeof caps);
        caps.dwSize = sizeof caps;
        if (SUCCEEDED(dev->lpVtbl->GetCapabilities(dev, &caps)))
        {
            L->ff = (caps.dwFlags & DIDC_FORCEFEEDBACK) ? 1 : 0;
            Log(L->ff ? "pad supports vibration" : "pad has no force feedback");
        }
    }

    if (g_eff[slot]) { g_eff[slot]->lpVtbl->Release(g_eff[slot]); g_eff[slot] = NULL; }
    g_dev[slot] = dev;
    return dev;
}

/* ------------- HID rumble fallback ------------- */

/* ---------------- HID path lookup ----------------
   RawInput hides devices filtered by HidHide and friends, so the HID class
   interface list is a second way to reach a pad path. Loaded dynamically so the
   hook never gains a new import the host game does not already have. */

static int CfgMatch(unsigned vid, unsigned pid, char *out, int cap)
{
    static HMODULE cm;
    static int tried;
    typedef int (WINAPI *FN_SIZE)(ULONG *, GUID *, const char *, ULONG);
    typedef int (WINAPI *FN_LIST)(GUID *, const char *, char *, ULONG, ULONG);
    static FN_SIZE fsize;
    static FN_LIST flist;
    if (!tried)
    {
        tried = 1;
        cm = LoadLibraryA("cfgmgr32.dll");
        if (cm)
        {
            fsize = (FN_SIZE)GetProcAddress(cm, "CM_Get_Device_Interface_List_SizeA");
            flist = (FN_LIST)GetProcAddress(cm, "CM_Get_Device_Interface_ListA");
        }
    }
    if (!fsize || !flist) return 0;

    GUID hid = { 0x4d1e55b2, 0xf16f, 0x11cf, { 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30 } };
    ULONG len = 0;
    if (fsize(&len, &hid, NULL, 2 /*present*/) != 0 || len == 0) return 0;
    char *buf = (char *)HeapAlloc(GetProcessHeap(), 0, len);
    if (!buf) return 0;
    int found = 0;
    if (flist(&hid, NULL, buf, len, 2) == 0)
    {
        char *p = buf;
        while (*p && !found)
        {
            unsigned v = 0, d = 0;
            const char *pv = strstr(p, "vid_");
            const char *pp = strstr(p, "pid_");
            if (pv && pp && sscanf(pv + 4, "%4x", &v) == 1 && sscanf(pp + 4, "%4x", &d) == 1
                && v == vid && d == pid)
            {
                lstrcpynA(out, p, cap);
                found = 1;
            }
            p += strlen(p) + 1;
        }
    }
    HeapFree(GetProcessHeap(), 0, buf);
    return found;
}

// Some pads (PS3/Android-mode ones included) report no force feedback through
// DirectInput but accept a rumble command as a HID output report. Find the
// device by the vendor/product in the slot token and open it for writing.
static HANDLE HidOpen(int slot)
{
    if (g_hid[slot] != INVALID_HANDLE_VALUE) return g_hid[slot];
    DWORD now = GetTickCount();
    if ((LONG)(now - g_hidRetry[slot]) < 0) return INVALID_HANDLE_VALUE;
    g_hidRetry[slot] = now + 500;   // throttle, but never stop trying
    g_hidTried[slot]++;
    if (strlen(diToken[slot]) < 6) return INVALID_HANDLE_VALUE;

    unsigned vid = 0, pid = 0;
    if (sscanf(diToken[slot] + 1, "%x_%x", &vid, &pid) != 2) return INVALID_HANDLE_VALUE;

    UINT n = 0;
    GetRawInputDeviceList(NULL, &n, sizeof(RAWINPUTDEVICELIST));
    if (!n) return INVALID_HANDLE_VALUE;
    RAWINPUTDEVICELIST *list = (RAWINPUTDEVICELIST *)HeapAlloc(GetProcessHeap(), 0, n * sizeof *list);
    if (!list) return INVALID_HANDLE_VALUE;
    GetRawInputDeviceList(list, &n, sizeof *list);

    HANDLE found = INVALID_HANDLE_VALUE;
    int foundLen = 0;
    for (UINT i = 0; i < n && found == INVALID_HANDLE_VALUE; i++)
    {
        if (list[i].dwType != RIM_TYPEHID) continue;
        RID_DEVICE_INFO info;
        memset(&info, 0, sizeof info);
        info.cbSize = sizeof info;
        UINT sz = sizeof info;
        if (GetRawInputDeviceInfoA(list[i].hDevice, RIDI_DEVICEINFO, &info, &sz) == (UINT)-1) continue;
        if (info.hid.dwVendorId != vid || info.hid.dwProductId != pid) continue;
        if (info.hid.usUsage != 0x05 && info.hid.usUsage != 0x04) continue;

        sz = 0;
        GetRawInputDeviceInfoA(list[i].hDevice, RIDI_DEVICENAME, NULL, &sz);
        if (!sz || sz == (UINT)-1) continue;
        char *path = (char *)HeapAlloc(GetProcessHeap(), 0, sz + 1);
        if (!path) continue;
        if (GetRawInputDeviceInfoA(list[i].hDevice, RIDI_DEVICENAME, path, &sz) == (UINT)-1) {
            HeapFree(GetProcessHeap(), 0, path);
            continue;
        }
        HANDLE h = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE)
            h = CreateFileA(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
        HeapFree(GetProcessHeap(), 0, path);
        if (h == INVALID_HANDLE_VALUE) continue;

        HMODULE hid = LoadLibraryA("hid.dll");
        if (hid)
        {
            PF_HIDP_CAPS caps;
            memset(&caps, 0, sizeof caps);
            PFN_HID_PREPARSED prep = (PFN_HID_PREPARSED)GetProcAddress(hid, "HidD_GetPreparsedData");
            PFN_HIDP_GETCAPS getcaps = (PFN_HIDP_GETCAPS)GetProcAddress(hid, "HidP_GetCaps");
            PVOID pp = NULL;
            if (prep && getcaps && prep(h, &pp) && pp)
            {
                if (getcaps(pp, &caps) >= 0 && caps.OutputReportByteLength >= 2)
                {
                    found = h;
                    foundLen = caps.OutputReportByteLength;
                    g_hidBt[slot] = IsBluetoothPathA(path);
                    char cm[96];
                    wsprintfA(cm, "hid caps out=%u in=%u feat=%u",
                              caps.OutputReportByteLength, caps.InputReportByteLength,
                              caps.FeatureReportByteLength);
                    Log(cm);
                }
            }
        }
        if (found == INVALID_HANDLE_VALUE) CloseHandle(h);
    }
    HeapFree(GetProcessHeap(), 0, list);

    // RawInput saw nothing: the pad may be hidden from this process. Ask the
    // device manager for the interface path instead.
    if (found == INVALID_HANDLE_VALUE)
    {
        char path[512];
        if (CfgMatch(vid, pid, path, sizeof path))
        {
            HANDLE h = CreateFileA(path, GENERIC_READ | GENERIC_WRITE,
                                   FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
            if (h == INVALID_HANDLE_VALUE)
                h = CreateFileA(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
            if (h != INVALID_HANDLE_VALUE)
            {
                HMODULE hid = LoadLibraryA("hid.dll");
                PFN_HID_PREPARSED prep = (PFN_HID_PREPARSED)GetProcAddress(hid, "HidD_GetPreparsedData");
                PFN_HIDP_GETCAPS getcaps = (PFN_HIDP_GETCAPS)GetProcAddress(hid, "HidP_GetCaps");
                PVOID pp = NULL;
                PF_HIDP_CAPS caps;
                memset(&caps, 0, sizeof caps);
                if (prep && getcaps && prep(h, &pp) && pp && getcaps(pp, &caps) >= 0
                    && caps.OutputReportByteLength >= 2)
                {
                    found = h;
                    foundLen = caps.OutputReportByteLength;
                    g_hidBt[slot] = IsBluetoothPathA(path);
                    Log("hid rumble available via cfgmgr32");
                }
                else CloseHandle(h);
            }
        }
    }

    g_hid[slot] = found;
    g_hidLen[slot] = foundLen;
    Log(found != INVALID_HANDLE_VALUE ? "hid rumble available" : "no hid rumble endpoint");
    return found;
}

static int HidWrite(int slot, const unsigned char *rep, int len)
{
    static HMODULE hid;
    static PFN_HID_SETOUTPUT set;
    static int tried;
    if (!tried)
    {
        tried = 1;
        hid = LoadLibraryA("hid.dll");
        if (hid) set = (PFN_HID_SETOUTPUT)GetProcAddress(hid, "HidD_SetOutputReport");
    }
    if (!set) return 0;
    if (set(g_hid[slot], (PVOID)rep, (ULONG)len)) { g_hidFails[slot] = 0; return 1; }

    // One failed report happens while the game re-enumerates the pad. Only a
    // run of failures means the handle is really gone.
    if (++g_hidFails[slot] < 5) return 0;
    char fm[64];
    wsprintfA(fm, "hid write failed slot %d, reopening", slot);
    Log(fm);
    CloseHandle(g_hid[slot]);
    g_hid[slot] = INVALID_HANDLE_VALUE;
    g_hidLayout[slot] = 0;
    g_hidFails[slot] = 0;
    return 0;
}

/* ---------------- PlayStation rumble ----------------
   SDL_hidapi_ps3/ps4/ps5 report layouts. These pads ignore every generic
   shape, so each family gets its exact report. */

static int IsBluetoothPathA(const char *path)
{
    if (!path) return 0;
    if (strstr(path, "BTHENUM") || strstr(path, "bthlenum")) return 1;
    if (strstr(path, "BTHLEDEVICE") || strstr(path, "bthledevice")) return 1;
    if (strstr(path, "00001124")) return 1;
    return 0;
}

/* 0 = not Sony, 1 = PS3, 2 = PS4, 3 = PS5. */
static int SonyKind(unsigned vid, unsigned pid)
{
    if (vid == 0x054C)
    {
        switch (pid)
        {
        case 0x0CE6: case 0x0DF2: case 0x0E5F: case 0x0BA0: return 3;
        case 0x05C4: case 0x09CC: case 0x05C5: case 0x0CDA: return 2;
        case 0x0268: case 0x0269: return 1;
        }
    }
    if (vid == 0x0F0D)
    {
        switch (pid)
        {
        case 0x00C1: case 0x005D: case 0x0066: return 1;
        case 0x00F0: case 0x00F1: case 0x00F2: case 0x00F3:
        case 0x00F4: case 0x00F5: case 0x00F6: case 0x00F7:
        case 0x00F8: case 0x00F9: case 0x00FA: return 2;
        }
    }
    return 0;
}

static unsigned SonyCrc32(unsigned crc, const unsigned char *d, int n)
{
    for (int i = 0; i < n; i++)
    {
        crc ^= d[i];
        for (int k = 0; k < 8; k++)
            crc = (crc & 1) ? (0xEDB88320u ^ (crc >> 1)) : (crc >> 1);
    }
    return crc;
}

static void SonySeal(unsigned char *rep, int size)
{
    unsigned char hdr = 0xA2;
    unsigned crc = SonyCrc32(0, &hdr, 1);
    crc = SonyCrc32(crc, rep, size - 4);
    rep[size - 4] = (unsigned char)crc;
    rep[size - 3] = (unsigned char)(crc >> 8);
    rep[size - 2] = (unsigned char)(crc >> 16);
    rep[size - 1] = (unsigned char)(crc >> 24);
}

static void SonyWrite(int slot, int kind, int bt, int left, int right)
{
    unsigned char rep[80];
    int size = 0;
    int off;

    memset(rep, 0, sizeof rep);

    if (kind == 1)
    {
        /* DS3: 49-byte effects report, id 1 at index 0. */
        rep[0] = 0x01;
        rep[1] = 0xFF;
        rep[2] = (unsigned char)(right ? 1 : 0);
        rep[3] = 0xFF;
        rep[4] = (unsigned char)left;
        rep[9] = (unsigned char)(0x01 << (1 + (slot % 4)));
        size = 49;
        if (!HidWrite(slot, rep, size))
        {
            /* sixaxis.sys style: report 0, command 2, motors at 6 and 8. */
            memset(rep, 0, sizeof rep);
            rep[0] = 0x00;
            rep[1] = 0x02;
            rep[5] = 0xFF;
            rep[6] = (unsigned char)(right ? 1 : 0);
            rep[7] = 0xFF;
            rep[8] = (unsigned char)left;
            HidWrite(slot, rep, 49);
        }
        return;
    }

    if (kind == 2)
    {
        /* DS4: rumble right then left, then the lightbar. */
        if (bt)
        {
            rep[0] = 0x11;
            rep[1] = 0xC0;            /* HID + CRC header, update interval 0 */
            rep[3] = 0x03;            /* rumble + LED */
            rep[6] = (unsigned char)right;
            rep[7] = (unsigned char)left;
            rep[8] = 0x00;
            rep[9] = 0x00;
            rep[10] = (unsigned char)(0x10 + slot * 0x16);
            size = 78;
            SonySeal(rep, size);
        }
        else
        {
            rep[0] = 0x05;
            rep[1] = 0x03;
            rep[4] = (unsigned char)right;
            rep[5] = (unsigned char)left;
            rep[6] = 0x00;
            rep[7] = 0x00;
            rep[8] = (unsigned char)(0x10 + slot * 0x16);
            size = 32;
        }
        HidWrite(slot, rep, size);
        return;
    }

    /* DualSense: 48-byte USB / 78-byte Bluetooth effect packet. */
    if (bt)
    {
        rep[0] = 0x31;
        rep[1] = 0x00;
        rep[2] = 0x10;
        size = 78;
    }
    else
    {
        rep[0] = 0x02;
        size = 48;
    }

    off = bt ? 3 : 1;
    if (left || right)
    {
        rep[off + 0] = 0x01;                 /* emulated rumble on */
        rep[off + 1] = 0x02;                 /* mute audio haptics */
        rep[off + 2] = (unsigned char)(right >> 1);
        rep[off + 3] = (unsigned char)(left >> 1);
    }
    rep[off + 1] |= 0x04;                    /* lightbar */
    rep[off + 44] = 0x00;
    rep[off + 45] = 0x00;
    rep[off + 46] = (unsigned char)(0x40 - slot * 0x08);
    rep[off + 10] = 0x05;                    /* right trigger: off */
    rep[off + 21] = 0x05;                    /* left trigger: off */
    if (bt) SonySeal(rep, size);
    HidWrite(slot, rep, size);
}

// Pads are picky about the output report: some want a leading report id, some
// do not, and the length has to match. Try the known shapes until one sticks
// and keep using that one afterwards.
static void HidSendRaw(int slot, int left, int right)
{
    unsigned vid = 0, pid = 0;
    sscanf(diToken[slot] + 1, "%x_%x", &vid, &pid);

    int sony = SonyKind(vid, pid);
    if (sony)
    {
        SonyWrite(slot, sony, g_hidBt[slot], left, right);
        if (g_hidLayout[slot] == 0)
        {
            g_hidLayout[slot] = 9;   /* fixed Sony protocol; skip the probe list */
            char m[64];
            wsprintfA(m, "hid rumble layout sony %d", sony);
            Log(m);
        }
        return;
    }

    int base = g_hidLen[slot];
    if (base < 2) base = 9;
    if (base > 16) base = 16;

    // ShanWan 2563:0575 (the "PC/PS3/Android" pad) speaks its own protocol,
    // documented by the hid-shanwan Linux driver: [0x02, 0x08, weak, strong,
    // 0xff, pad...]. The generic byte layout below never made it rumble.
    if (vid == 0x2563 && pid == 0x0575)
    {
        unsigned char rep[16];
        memset(rep, 0, sizeof rep);
        rep[0] = 0x02;
        rep[1] = 0x08;
        rep[2] = (unsigned char)right;   // weak / small motor
        rep[3] = (unsigned char)left;    // strong / big motor
        // 0xff means "run until told otherwise" (the only duration the
        // firmware documents). The stop report must therefore use 0x00 -
        // sending 0xff with zero magnitudes kept the motors running at the
        // last strength, which is why the pad buzzed forever.
        rep[4] = (left > 0 || right > 0) ? 0xFF : 0x00;
        HidWrite(slot, rep, base);
        if (g_hidLayout[slot] == 0 && g_hid[slot] != INVALID_HANDLE_VALUE)
        {
            g_hidLayout[slot] = 7;
            char m[64];
            wsprintfA(m, "hid rumble layout shanwan len %d", base);
            Log(m);
        }
        return;
    }

    static const int order[6][2] = {   // {first byte, report length}
        { 0x00, 9 },
        { 0x00, 8 },
        { 0x01, 9 },
        { 0x00, 7 },
        { 0x00, 6 },
        { 0x00, 4 }
    };

    int first = 0, last = 6;
    if (g_hidLayout[slot] > 0 && g_hidLayout[slot] <= 6) { first = g_hidLayout[slot] - 1; last = first + 1; }
    else { g_hidLayout[slot] = 0; }

    for (int k = first; k < last; k++)
    {
        int len = order[k][1];
        if (k == 0) len = base;            // prefer the size HID reported

        unsigned char rep[16];
        memset(rep, 0, sizeof rep);

        rep[0] = (unsigned char)order[k][0];
        rep[3] = (unsigned char)right;
        rep[4] = (unsigned char)left;

        HidWrite(slot, rep, len);
        if (g_hid[slot] == INVALID_HANDLE_VALUE) return;
        if (g_hidLayout[slot] == 0)
        {
            g_hidLayout[slot] = k + 1;
            char m[64];
            wsprintfA(m, "hid rumble layout %d len %d", k + 1, len);
            Log(m);
        }
        return;
    }
}

// A single output report only turns the motors on for a moment on some pads, so
// the rumble is re-sent while it lasts and one zero report is sent when it ends.
static DWORD WINAPI HidPump(LPVOID unused)
{
    (void)unused;
    for (;;)
    {
        Sleep(50);
        DWORD now = GetTickCount();
        for (int s = 0; s < 4; s++)
        {
            if (g_hid[s] == INVALID_HANDLE_VALUE) continue;
            if (g_hidIdle[s]) continue;
            if ((LONG)(now - g_hidUntil[s]) >= 0)
            {
                g_hidL[s] = 0;
                g_hidR[s] = 0;
                HidSendRaw(s, 0, 0);
                g_hidIdle[s] = 1;
                continue;
            }
            HidSendRaw(s, g_hidL[s], g_hidR[s]);
        }
    }
    return 0;
}

// ms is how long the command stays in effect. Games call XInputSetState every
// frame while the pad should rumble, so each call refreshes the window; when
// the calls stop the pump sends the stop report. Without this the firmware
// keeps the motors running on the last command.
static void HidSet(int slot, int left, int right, int ms)
{
    if (left < 0) left = 0;
    if (right < 0) right = 0;
    if (left > 255) left = 255;
    if (right > 255) right = 255;

    int pair = (left << 8) | right;
    if (pair != g_hidLogged[slot] && (pair != 0 || g_hidLogged[slot] != 0))
    {
        g_hidLogged[slot] = pair;
        char cm[64];
        wsprintfA(cm, "hid rumble asked slot %d l=%d r=%d", slot, left, right);
        Log(cm);
    }

    if (left == 0 && right == 0)
    {
        g_hidL[slot] = 0;
        g_hidR[slot] = 0;
        g_hidLast[slot] = GetTickCount();
        g_hidUntil[slot] = 0;
        g_hidIdle[slot] = 1;
        if (g_hid[slot] != INVALID_HANDLE_VALUE) HidSendRaw(slot, 0, 0);
        return;
    }

    g_hidL[slot] = left;
    g_hidR[slot] = right;
    g_hidLast[slot] = GetTickCount();
    g_hidUntil[slot] = g_hidLast[slot] + (DWORD)(ms > 0 ? ms : 150);
    g_hidIdle[slot] = 0;

    if (HidOpen(slot) == INVALID_HANDLE_VALUE) return;
    HidSendRaw(slot, left, right);

    if (InterlockedExchange(&g_hidPump, 1) == 0)
        CreateThread(NULL, 0, HidPump, NULL, 0, NULL);
}

/* ---------------- helpers ---------------- */

// The real DLL is the one we stand in for, by the same name but from the
// system directory. A different name would shift every ordinal and break games
// that import XInput by ordinal (FIFA 12 imports ordinal 2, 3 and 4).
static HMODULE RealModule(void)
{
    static HMODULE m;
    static int tried;
    if (tried) return m;
    tried = 1;

    char self[MAX_PATH], sysdir[MAX_PATH], path[MAX_PATH];
    if (!GetModuleFileNameA(g_self, self, MAX_PATH)) return NULL;
    char *name = strrchr(self, '\\');
    if (!name) return NULL;
    name++;
    if (!GetSystemDirectoryA(sysdir, MAX_PATH)) return NULL;
    wsprintfA(path, "%s\\%s", sysdir, name);
    m = LoadLibraryExA(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    return m;
}

static void *RealProc(const char *name)
{
    HMODULE m = RealModule();
    return m ? (void *)GetProcAddress(m, name) : NULL;
}

static void Log(const char *msg)
{
    char path[MAX_PATH];
    GetModuleFileNameA(NULL, path, MAX_PATH);
    char *slash = strrchr(path, '\\');
    if (slash) *(slash + 1) = 0;
    strcat(path, "mapivo-hook.log");
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ, NULL,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, NULL, FILE_END);
    char line[512];
    wsprintfA(line, "[proxy %lu] %s\r\n", GetCurrentProcessId(), msg);
    DWORD w;
    WriteFile(h, line, (DWORD)strlen(line), &w, NULL);
    CloseHandle(h);
}

static int TargetIndex(const char *name)
{
    for (int t = 0; t < T_COUNT; t++)
        if (strcmp(TARGET_NAME[t], name) == 0) return t;
    return -1;
}

// A DirectInput pad is addressed by raw index ("b8"); an XInput pad by a
// canonical name ("back"). A stale line of the wrong kind used to make Back
// and Start read the same physical bit, so those lines are dropped here.
static int SourceAllowed(const char *name, int directInput)
{
    if (name[0] == 'b' && name[1] >= '0' && name[1] <= '9') return directInput;
    if (TargetIndex(name) >= 0) return 1;
    if (strcmp(name, "lx+") == 0 || strcmp(name, "lx-") == 0) return 1;
    if (strcmp(name, "ly+") == 0 || strcmp(name, "ly-") == 0) return 1;
    if (strcmp(name, "rx+") == 0 || strcmp(name, "rx-") == 0) return 1;
    if (strcmp(name, "ry+") == 0 || strcmp(name, "ry-") == 0) return 1;
    return 0;
}

static unsigned long long g_cfgStamp;
static void ReadConfig(void)
{
    InitDefaults();

    char path[MAX_PATH];
    GetModuleFileNameA(NULL, path, MAX_PATH);
    char *slash = strrchr(path, '\\');
    if (slash) *(slash + 1) = 0;
    strcat(path, "mapivo.ini");

    char buf[4096] = { 0 };
    HANDLE h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        g_cfgStamp = 0;
        cfgPresent = 0;
        Log("no mapivo.ini, auto-detect");
        return;
    }
    cfgPresent = 1;
    FILETIME ft;
    GetFileTime(h, NULL, NULL, &ft);
    g_cfgStamp = ((unsigned long long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    DWORD rd = 0;
    ReadFile(h, buf, sizeof(buf) - 1, &rd, NULL);
    CloseHandle(h);

    char *line = buf;
    while (line && *line) {
        char *next = strchr(line, '\n');
        if (next) *next++ = 0;

        if (line[0] >= '0' && line[0] <= '3' && line[1] == '.') {
            int slot = line[0] - '0';
            char *key = line + 2;
            char *eq = strchr(key, '=');
            if (eq) {
                *eq = 0;
                char *val = eq + 1;
                Trim(key);
                Trim(val);

                if (strcmp(key, "src") == 0) {
                    map[slot] = -1;
                    diToken[slot][0] = 0;
                    if (val[0] == 'x' || val[0] == 'X') {
                        map[slot] = atoi(val + 1);
                    } else if (val[0] == 'j' || val[0] == 'J') {
                        strncpy(diToken[slot], val, sizeof(diToken[slot]) - 1);
                        diToken[slot][sizeof(diToken[slot]) - 1] = 0;
                    }
                } else if (strcmp(key, "vib") == 0) {
                    vib[slot] = atoi(val) ? 1 : 0;
                } else {
                    int t = TargetIndex(key);
                    if (t >= 0) {
                        strncpy(slotMap[slot][t], val, sizeof(slotMap[slot][t]) - 1);
                        slotMap[slot][t][sizeof(slotMap[slot][t]) - 1] = 0;
                        mapped[slot] = 1;
                    }
                }
            }
        }
        line = next;
    }
    for (int s = 0; s < 4; s++)
    {
        int di = diToken[s][0] ? 1 : 0;
        for (int t = 0; t < T_COUNT; t++)
            if (!SourceAllowed(slotMap[s][t], di))
            {
                Log("ignored a mapping this pad cannot use");
                strcpy(slotMap[s][t], DefaultSource(t));
            }
        BuildRoute(s);
    }
    Log("config loaded");
}

// Raw HID buttons map 1:1 onto canonical buttons. The app learns the physical
// button and stores it as "bN", which reads the same index here.
// The app writes mapivo.ini next to the game, so re-reading it whenever it
// changes means mappings stay in effect even when the app is not running.
static void WatchConfig(void)
{
    char path[MAX_PATH];
    GetModuleFileNameA(NULL, path, MAX_PATH);
    char *slash = strrchr(path, '\\');
    if (slash) *(slash + 1) = 0;
    strcat(path, "mapivo.ini");

    WIN32_FILE_ATTRIBUTE_DATA fa;
    unsigned long long stamp = 0;
    if (GetFileAttributesExA(path, GetFileExInfoStandard, &fa))
        stamp = ((unsigned long long)fa.ftLastWriteTime.dwHighDateTime << 32)
              | fa.ftLastWriteTime.dwLowDateTime;
    if (stamp != g_cfgStamp) ReadConfig();
}

static DWORD WINAPI InitThread(LPVOID p)
{
    (void)p;
    if (!loaded) { loaded = 1; ReadConfig(); }
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = inst;
        DisableThreadLibraryCalls(inst);
        HANDLE t = CreateThread(NULL, 0, InitThread, NULL, 0, NULL);
        if (t) CloseHandle(t);
    }
    return TRUE;
}

/* ---------------- raw pad state ---------------- */

// Axes are -32768..32767; triggers 0..32767; btn = canonical buttons; phys = raw buttons.
typedef struct { int lx, ly, rx, ry, lt, rt; unsigned btn; unsigned phys; } RAW;

enum { B_A, B_B, B_X, B_Y, B_LB, B_RB, B_BACK, B_START, B_L3, B_R3,
       B_UP, B_DOWN, B_LEFT, B_RIGHT };

// Triggers are buttons on some pads (per SDL's device database) and axes on
// others, so they are resolved the same way as the other 14 controls.
static const char *BUTTON_NAME[16] = {
    "a", "b", "x", "y", "lb", "rb", "back", "start", "l3", "r3",
    "up", "down", "left", "right", "lt", "rt"
};

static int AxisValue(int v)
{
    if (v < 0) return 0;
    if (v > 32767) return 65535;
    return v + 32768;
}

static int SourceValue(const RAW *r, const char *name, int slot)
{
    if (!name || !name[0]) return 0;

    if (strcmp(name, "lx") == 0) return AxisValue(r->lx);
    if (strcmp(name, "ly") == 0) return AxisValue(r->ly);
    if (strcmp(name, "rx") == 0) return AxisValue(r->rx);
    if (strcmp(name, "ry") == 0) return AxisValue(r->ry);
    // Stick directions, so a button or D-pad direction can be driven by
    // pushing a stick ("lx+", "ry-", ...). Threshold matches the UI.
    if (name[0] == 'l' || name[0] == 'r') {
        int v = 0, ok = 1;
        if (strcmp(name, "lx+") == 0) v = r->lx;
        else if (strcmp(name, "lx-") == 0) v = -r->lx;
        else if (strcmp(name, "ly+") == 0) v = r->ly;
        else if (strcmp(name, "ly-") == 0) v = -r->ly;
        else if (strcmp(name, "rx+") == 0) v = r->rx;
        else if (strcmp(name, "rx-") == 0) v = -r->rx;
        else if (strcmp(name, "ry+") == 0) v = r->ry;
        else if (strcmp(name, "ry-") == 0) v = -r->ry;
        else ok = 0;
        if (ok) return v >= 16384 ? 65535 : 0;
    }

    // DirectInput pads have no fixed button order, so a named source and the
    // raw index only agree through the map. route[target] tells us which
    // canonical bit a raw HID index stands for on this pad.
    for (int b = 0; b < 16; b++)
        if (strcmp(name, BUTTON_NAME[b]) == 0)
        {
            if (slot >= 0 && slot < 4 && diToken[slot][0])
            {
                // D-pad directions come from the POV hat, not a HID button,
                // so they must stay reachable even when the map has an entry.
                if (route[slot][b] < 0 && b >= 10 && b < 14)
                    return (r->btn & (1u << b)) ? 65535 : 0;
                int bit = route[slot][b] >= 0 ? route[slot][b] : b;
                if (bit < 32) return (r->phys & (1u << bit)) ? 65535 : 0;
                if (b >= 10 && b < 14) return (r->btn & (1u << b)) ? 65535 : 0;
                if (b == 14) return r->lt;
                if (b == 15) return r->rt;
                return 0;
            }
            if (b == 14) return r->lt;
            if (b == 15) return r->rt;
            return (r->btn & (1u << b)) ? 65535 : 0;
        }

    if (name[0] == 'b' && name[1] >= '0' && name[1] <= '9') {
        int i = atoi(name + 1);
        if (i >= 0 && i < 32) return (r->phys & (1u << i)) ? 65535 : 0;
    }
    return 0;
}

static int Val(const RAW *r, int slot, int target)
{
    return SourceValue(r, slotMap[slot][target], slot);
}

static void RawFromXInput(const PAD_STATE *s, RAW *r)
{
    memset(r, 0, sizeof *r);
    r->lx = s->Gamepad.sThumbLX;
    r->ly = s->Gamepad.sThumbLY;
    r->rx = s->Gamepad.sThumbRX;
    r->ry = s->Gamepad.sThumbRY;
    r->lt = s->Gamepad.bLeftTrigger * 257;
    r->rt = s->Gamepad.bRightTrigger * 257;

    WORD b = s->Gamepad.wButtons;
    if (b & 0x1000) r->btn |= 1u << B_A;
    if (b & 0x2000) r->btn |= 1u << B_B;
    if (b & 0x4000) r->btn |= 1u << B_X;
    if (b & 0x8000) r->btn |= 1u << B_Y;
    if (b & 0x0100) r->btn |= 1u << B_LB;
    if (b & 0x0200) r->btn |= 1u << B_RB;
    if (b & 0x0020) r->btn |= 1u << B_BACK;
    if (b & 0x0010) r->btn |= 1u << B_START;
    if (b & 0x0040) r->btn |= 1u << B_L3;
    if (b & 0x0080) r->btn |= 1u << B_R3;
    if (b & 0x0001) r->btn |= 1u << B_UP;
    if (b & 0x0002) r->btn |= 1u << B_DOWN;
    if (b & 0x0004) r->btn |= 1u << B_LEFT;
    if (b & 0x0008) r->btn |= 1u << B_RIGHT;
}

static void BuildMapped(const RAW *r, int slot, PAD_GAMEPAD *g)
{
    int v;
    memset(g, 0, sizeof *g);

    if (Val(r, slot, T_A) > 32767) g->wButtons |= 0x1000;
    if (Val(r, slot, T_B) > 32767) g->wButtons |= 0x2000;
    if (Val(r, slot, T_X) > 32767) g->wButtons |= 0x4000;
    if (Val(r, slot, T_Y) > 32767) g->wButtons |= 0x8000;
    if (Val(r, slot, T_LB) > 32767) g->wButtons |= 0x0100;
    if (Val(r, slot, T_RB) > 32767) g->wButtons |= 0x0200;
    if (Val(r, slot, T_BACK) > 32767) g->wButtons |= 0x0020;
    if (Val(r, slot, T_START) > 32767) g->wButtons |= 0x0010;
    if (Val(r, slot, T_L3) > 32767) g->wButtons |= 0x0040;
    if (Val(r, slot, T_R3) > 32767) g->wButtons |= 0x0080;
    if (Val(r, slot, T_UP) > 32767) g->wButtons |= 0x0001;
    if (Val(r, slot, T_DOWN) > 32767) g->wButtons |= 0x0002;
    if (Val(r, slot, T_LEFT) > 32767) g->wButtons |= 0x0004;
    if (Val(r, slot, T_RIGHT) > 32767) g->wButtons |= 0x0008;

    v = Val(r, slot, T_LT); g->bLeftTrigger = (BYTE)(v / 257);
    v = Val(r, slot, T_RT); g->bRightTrigger = (BYTE)(v / 257);

    // Sticks are driven by four independent direction targets per stick.
    g->sThumbLX = Val(r, slot, T_LR) > 32767 ? 32767
                : Val(r, slot, T_LL) > 32767 ? -32767 : 0;
    g->sThumbLY = Val(r, slot, T_LU) > 32767 ? 32767
                : Val(r, slot, T_LD) > 32767 ? -32767 : 0;
    g->sThumbRX = Val(r, slot, T_RR) > 32767 ? 32767
                : Val(r, slot, T_RL) > 32767 ? -32767 : 0;
    g->sThumbRY = Val(r, slot, T_RU) > 32767 ? 32767
                : Val(r, slot, T_RD) > 32767 ? -32767 : 0;
}

// DirectInput usually reports a stick as 0..65535 with rest at the middle.
// XInput wants -32768..32767 around 0, so shift by the rest point first.
static int Stick(int v, int mn, int mx)
{
    int rest = (mn + mx) / 2;
    int span = (rest - mn) > (mx - rest) ? (rest - mn) : (mx - rest);
    if (span <= 0) return (short)v;
    int d = (v - rest) * 32767 / span;
    return d < -32768 ? -32768 : d > 32767 ? 32767 : d;
}

// Distance from the rest position, scaled to 0..65535 by the longest side of
// the axis range. A centred signed axis rests at 0; an unsigned one at its min.
static int Trigger(int v, int mn, int mx)
{
    int rest = (mn < 0 && mx > 0) ? 0 : mn;
    int span = (rest - mn) > (mx - rest) ? (rest - mn) : (mx - rest);
    if (span <= 0) return 0;
    int d = v - rest;
    if (d < 0) d = -d;
    if (d > span) d = span;
    return d * 65535 / span;
}

// For a DirectInput pad, every canonical button source resolves through this
// table to the device's real HID button index. Entries come from the app's
// per-device map (SDL gamecontrollerdb defaults, or what the user mapped).
static void BuildRoute(int slot)
{
    if (!diToken[slot][0]) return;
    for (int b = 0; b < 16; b++) route[slot][b] = -1;

    // Sony pads are read from their own report, where the canonical button
    // bits already line up; a stored map written for DirectInput indices
    // would only garble them.
    unsigned svid = 0, spid = 0;
    if (sscanf(diToken[slot] + 1, "%x_%x", &svid, &spid) == 2 && SonyKind(svid, spid))
        return;
    for (int t = 0; t < 16; t++)
    {
        const char *src = slotMap[slot][t];
        int idx = -1;
        if (src[0] == 'b' && src[1] >= '0' && src[1] <= '9') idx = atoi(src + 1);
        else if (src[0] >= '0' && src[0] <= '9') idx = atoi(src);
        if (idx >= 0 && idx < 32) route[slot][t] = idx;
    }
}

static int RawFromDI(int slot, RAW *r)
{
    memset(r, 0, sizeof *r);
    IDirectInputDevice8W *dev = DiDevice(slot);
    if (!dev) return 0;

    DILAYOUT *L = &g_lay[slot];
    BYTE buf[256];
    HRESULT hr = dev->lpVtbl->Poll(dev);
    if (FAILED(hr)) hr = dev->lpVtbl->Acquire(dev);
    if (SUCCEEDED(hr)) hr = dev->lpVtbl->GetDeviceState(dev, L->size, buf);
    if (FAILED(hr)) {
        // One more try: the device is usually only lost to focus.
        hr = dev->lpVtbl->Acquire(dev);
        if (SUCCEEDED(hr)) hr = dev->lpVtbl->GetDeviceState(dev, L->size, buf);
    }
    if (FAILED(hr)) {
        // Gone for good (unplugged, or the slot went stale): drop it so the
        // next call enumerates again and the pad works after a replug.
        DropDevice(slot);
        return 0;
    }

    #define AX(ofs, fb) ((ofs) >= 0 ? *(LONG *)(buf + (ofs)) : (fb))
    r->lx =  Stick(AX(L->lx, 0), L->stMin[0], L->stMax[0]);
    r->ly = -Stick(AX(L->ly, 0), L->stMin[1], L->stMax[1]);
    r->rx =  Stick(AX(L->rx, 0), L->stMin[2], L->stMax[2]);
    r->ry = -Stick(AX(L->ry, 0), L->stMin[3], L->stMax[3]);
    // Triggers idle at 0 on some pads and at 32767 (centred signed axis) on
    // others. Distance from centre reads 0 at rest either way.
    r->lt = L->lz  >= 0 ? Trigger(AX(L->lz, 0),  L->trMin[0], L->trMax[0]) : 0;
    r->rt = L->lrz >= 0 ? Trigger(AX(L->lrz, 0), L->trMin[1], L->trMax[1]) : 0;
    #undef AX

    // Raw HID index is the canonical button index, so "bN" and the named
    // buttons ("a", "x", ...) always agree with the live test.
    for (int i = 0; i < L->btnCount && i < 32; i++)
        if (buf[L->btn[i]]) {
            r->phys |= 1u << i;
            if (i < 10) r->btn |= 1u << i;
        }

    DWORD pov = L->pov >= 0 ? *(DWORD *)(buf + L->pov) : 0xFFFFFFFF;
    if (pov != 0xFFFFFFFF) {
        DWORD deg = pov / 100;
        if (deg >= 315 || deg < 45)  r->btn |= 1u << B_UP;
        if (deg >= 45  && deg < 135) r->btn |= 1u << B_RIGHT;
        if (deg >= 135 && deg < 225) r->btn |= 1u << B_DOWN;
        if (deg >= 225 && deg < 315) r->btn |= 1u << B_LEFT;
    }
    return 1;
}

/* ---------------- PlayStation input ----------------
   Read the pad's own HID report instead of DirectInput. DirectInput often
   reports the triggers as buttons and numbers the face buttons differently per
   driver, while the report layout is fixed. Non-blocking: at most one read is
   outstanding and a poll that finds nothing new costs a wait of 0. */

typedef struct
{
    HANDLE h;
    OVERLAPPED ov;
    int kind;
    int bt;
    int pending;
    DWORD retry;
    unsigned char buf[80];
    int len;
} SONYPAD;

static SONYPAD g_sp[4];
static int g_spInit;

static void SonyPadInit(void)
{
    if (g_spInit) return;
    g_spInit = 1;
    for (int i = 0; i < 4; i++)
    {
        g_sp[i].h = INVALID_HANDLE_VALUE;
        g_sp[i].ov.hEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
    }
}

static int SonyOpen(int slot, unsigned vid, unsigned pid, int kind)
{
    DWORD now = GetTickCount();
    if ((LONG)(now - g_sp[slot].retry) < 0) return 0;
    g_sp[slot].retry = now + 1000;

    char path[512] = "";
    int bt = 0;

    UINT n = 0;
    GetRawInputDeviceList(NULL, &n, sizeof(RAWINPUTDEVICELIST));
    if (n)
    {
        RAWINPUTDEVICELIST *list = (RAWINPUTDEVICELIST *)HeapAlloc(GetProcessHeap(), 0, n * sizeof *list);
        if (list && GetRawInputDeviceList(list, &n, sizeof *list) != (UINT)-1)
        {
            for (UINT i = 0; i < n && !path[0]; i++)
            {
                if (list[i].dwType != RIM_TYPEHID) continue;
                RID_DEVICE_INFO info;
                memset(&info, 0, sizeof info);
                info.cbSize = sizeof info;
                UINT sz = sizeof info;
                if (GetRawInputDeviceInfoA(list[i].hDevice, RIDI_DEVICEINFO, &info, &sz) == (UINT)-1) continue;
                if (info.hid.dwVendorId != vid || info.hid.dwProductId != pid) continue;
                if (info.hid.usUsage != 0x04 && info.hid.usUsage != 0x05) continue;
                sz = 0;
                GetRawInputDeviceInfoA(list[i].hDevice, RIDI_DEVICENAME, NULL, &sz);
                if (!sz || sz == (UINT)-1) continue;
                char *tmp = (char *)HeapAlloc(GetProcessHeap(), 0, sz + 1);
                if (!tmp) continue;
                if (GetRawInputDeviceInfoA(list[i].hDevice, RIDI_DEVICENAME, tmp, &sz) != (UINT)-1)
                    lstrcpynA(path, tmp, sizeof path);
                HeapFree(GetProcessHeap(), 0, tmp);
            }
        }
        if (list) HeapFree(GetProcessHeap(), 0, list);
    }
    if (!path[0] && !CfgMatch(vid, pid, path, sizeof path)) return 0;
    bt = IsBluetoothPathA(path);

    HANDLE h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;

    g_sp[slot].h = h;
    g_sp[slot].kind = kind;
    g_sp[slot].bt = bt;
    g_sp[slot].pending = 0;
    g_sp[slot].len = 0;
    return 1;
}

static int SonyAxis(unsigned char v) { return (int)v * 257 - 32768; }

static void SonyParse(int slot, const unsigned char *d, int len, RAW *r)
{
    int kind = g_sp[slot].kind;
    int p;

    memset(r, 0, sizeof *r);

    if (kind == 1)
    {
        if (len < 7 || d[0] != 0x01 || d[1] == 0xFF) return;

        if (len <= 9)
        {
            /* ShanWan PS2 -> PS3 mini packet. */
            r->rx = SonyAxis(d[0]); r->ry = SonyAxis(d[1]);
            r->lx = SonyAxis(d[2]); r->ly = SonyAxis(d[3]);
            int face = d[4], sh = d[5];
            /* Low nibble = D-pad hat, high nibble = shape buttons. */
            switch (face & 0x0F)
            {
            case 0: r->btn |= 1u << B_UP; break;
            case 1: r->btn |= (1u << B_UP) | (1u << B_RIGHT); break;
            case 2: r->btn |= 1u << B_RIGHT; break;
            case 3: r->btn |= (1u << B_RIGHT) | (1u << B_DOWN); break;
            case 4: r->btn |= 1u << B_DOWN; break;
            case 5: r->btn |= (1u << B_DOWN) | (1u << B_LEFT); break;
            case 6: r->btn |= 1u << B_LEFT; break;
            case 7: r->btn |= (1u << B_LEFT) | (1u << B_UP); break;
            }
            int mface = face >> 4;
            if (mface & 0x01) { r->btn |= 1u << B_Y; r->phys |= 1u << 0; }
            if (mface & 0x02) { r->btn |= 1u << B_B; r->phys |= 1u << 1; }
            if (mface & 0x04) { r->btn |= 1u << B_A; r->phys |= 1u << 2; }
            if (mface & 0x08) { r->btn |= 1u << B_X; r->phys |= 1u << 3; }
            if (sh & 0x01) { r->btn |= 1u << B_LB; r->phys |= 1u << 4; }
            if (sh & 0x02) { r->btn |= 1u << B_RB; r->phys |= 1u << 5; }
            if (sh & 0x10) { r->btn |= 1u << B_BACK; r->phys |= 1u << 9; }
            if (sh & 0x20) { r->btn |= 1u << B_START; r->phys |= 1u << 8; }
            if (sh & 0x40) { r->btn |= 1u << B_L3; r->phys |= 1u << 10; }
            if (sh & 0x80) { r->btn |= 1u << B_R3; r->phys |= 1u << 11; }
            r->lt = (sh & 0x04) ? 32767 : 0;
            r->rt = (sh & 0x08) ? 32767 : 0;
            return;
        }

        if (len < 20) return;
        int b2 = d[2], b3 = d[3];
        if (b2 & 0x01) { r->btn |= 1u << B_BACK; r->phys |= 1u << 9; }
        if (b2 & 0x02) { r->btn |= 1u << B_L3; r->phys |= 1u << 10; }
        if (b2 & 0x04) { r->btn |= 1u << B_R3; r->phys |= 1u << 11; }
        if (b2 & 0x08) { r->btn |= 1u << B_START; r->phys |= 1u << 8; }
        if (b2 & 0x10) r->btn |= 1u << B_UP;
        if (b2 & 0x20) r->btn |= 1u << B_RIGHT;
        if (b2 & 0x40) r->btn |= 1u << B_DOWN;
        if (b2 & 0x80) r->btn |= 1u << B_LEFT;
        if (b3 & 0x04) { r->btn |= 1u << B_LB; r->phys |= 1u << 6; }
        if (b3 & 0x08) { r->btn |= 1u << B_RB; r->phys |= 1u << 7; }
        /* PS3 order: 0x10 triangle, 0x20 circle, 0x40 cross, 0x80 square. */
        int face = b3 >> 4;
        if (face & 0x01) { r->btn |= 1u << B_Y; r->phys |= 1u << 0; }
        if (face & 0x02) { r->btn |= 1u << B_B; r->phys |= 1u << 1; }
        if (face & 0x04) { r->btn |= 1u << B_A; r->phys |= 1u << 2; }
        if (face & 0x08) { r->btn |= 1u << B_X; r->phys |= 1u << 3; }
        if (d[4] & 0x01) r->phys |= 1u << 12;
        r->lx = SonyAxis(d[6]); r->ly = SonyAxis(d[7]);
        r->rx = SonyAxis(d[8]); r->ry = SonyAxis(d[9]);
        r->lt = d[18] * 257 / 2; r->rt = d[19] * 257 / 2;
        return;
    }

    if (kind == 2)
    {
        if (d[0] == 0x01) p = 1;
        else if (d[0] >= 0x11 && d[0] <= 0x19)
        {
            if (len < 10 || !(d[1] & 0x80)) return;
            p = 3;
        }
        else return;
        if (len < p + 9) return;

        r->lx = SonyAxis(d[p]); r->ly = SonyAxis(d[p + 1]);
        r->rx = SonyAxis(d[p + 2]); r->ry = SonyAxis(d[p + 3]);
        int b0 = d[p + 4], b1 = d[p + 5], b2 = d[p + 6];
        int face = b0 >> 4;
        if (face & 0x01) { r->btn |= 1u << B_X; r->phys |= 1u; }
        if (face & 0x02) { r->btn |= 1u << B_A; r->phys |= 1u << 1; }
        if (face & 0x04) { r->btn |= 1u << B_B; r->phys |= 1u << 2; }
        if (face & 0x08) { r->btn |= 1u << B_Y; r->phys |= 1u << 3; }
        switch (b0 & 0x0F)
        {
        case 0: r->btn |= 1u << B_UP; break;
        case 1: r->btn |= (1u << B_UP) | (1u << B_RIGHT); break;
        case 2: r->btn |= 1u << B_RIGHT; break;
        case 3: r->btn |= (1u << B_RIGHT) | (1u << B_DOWN); break;
        case 4: r->btn |= 1u << B_DOWN; break;
        case 5: r->btn |= (1u << B_DOWN) | (1u << B_LEFT); break;
        case 6: r->btn |= 1u << B_LEFT; break;
        case 7: r->btn |= (1u << B_LEFT) | (1u << B_UP); break;
        }
        if (b1 & 0x01) { r->btn |= 1u << B_LB; r->phys |= 1u << 4; }
        if (b1 & 0x02) { r->btn |= 1u << B_RB; r->phys |= 1u << 5; }
        if (b1 & 0x10) { r->btn |= 1u << B_BACK; r->phys |= 1u << 8; }
        if (b1 & 0x20) { r->btn |= 1u << B_START; r->phys |= 1u << 9; }
        if (b1 & 0x40) { r->btn |= 1u << B_L3; r->phys |= 1u << 10; }
        if (b1 & 0x80) { r->btn |= 1u << B_R3; r->phys |= 1u << 11; }
        int lt = d[p + 7], rt = d[p + 8];
        r->lt = (lt == 0 && (b1 & 0x04)) ? 32767 : lt * 257 / 2;
        r->rt = (rt == 0 && (b1 & 0x08)) ? 32767 : rt * 257 / 2;
        if (b2 & 0x01) r->phys |= 1u << 12;
        if (b2 & 0x02) r->phys |= 1u << 13;
        return;
    }

    /* DualSense */
    if (d[0] == 0x01) p = 1;
    else if (d[0] == 0x31)
    {
        if (len < 12) return;
        p = 2;
    }
    else return;

    if (len == 10 || len == 78)
    {
        /* Simple report: sticks, one buttons+hat byte, then the triggers. */
        if (len < 9) return;
        int c0 = d[p + 4];
        r->lx = SonyAxis(d[p]); r->ly = SonyAxis(d[p + 1]);
        r->rx = SonyAxis(d[p + 2]); r->ry = SonyAxis(d[p + 3]);
        int face = c0 >> 4;
        if (face & 0x01) { r->btn |= 1u << B_X; r->phys |= 1u; }
        if (face & 0x02) { r->btn |= 1u << B_A; r->phys |= 1u << 1; }
        if (face & 0x04) { r->btn |= 1u << B_B; r->phys |= 1u << 2; }
        if (face & 0x08) { r->btn |= 1u << B_Y; r->phys |= 1u << 3; }
        switch (c0 & 0x0F)
        {
        case 0: r->btn |= 1u << B_UP; break;
        case 1: r->btn |= (1u << B_UP) | (1u << B_RIGHT); break;
        case 2: r->btn |= 1u << B_RIGHT; break;
        case 3: r->btn |= (1u << B_RIGHT) | (1u << B_DOWN); break;
        case 4: r->btn |= 1u << B_DOWN; break;
        case 5: r->btn |= (1u << B_DOWN) | (1u << B_LEFT); break;
        case 6: r->btn |= 1u << B_LEFT; break;
        case 7: r->btn |= (1u << B_LEFT) | (1u << B_UP); break;
        }
        r->lt = d[p + 5] * 257 / 2;
        r->rt = d[p + 6] * 257 / 2;
        return;
    }

    if (len < p + 10) return;
    r->lx = SonyAxis(d[p]); r->ly = SonyAxis(d[p + 1]);
    r->rx = SonyAxis(d[p + 2]); r->ry = SonyAxis(d[p + 3]);
    int lt = d[p + 4], rt = d[p + 5];
    int a = d[p + 7], b = d[p + 8], c = d[p + 9];
    int face = a >> 4;
    if (face & 0x01) { r->btn |= 1u << B_X; r->phys |= 1u; }
    if (face & 0x02) { r->btn |= 1u << B_A; r->phys |= 1u << 1; }
    if (face & 0x04) { r->btn |= 1u << B_B; r->phys |= 1u << 2; }
    if (face & 0x08) { r->btn |= 1u << B_Y; r->phys |= 1u << 3; }
    switch (a & 0x0F)
    {
    case 0: r->btn |= 1u << B_UP; break;
    case 1: r->btn |= (1u << B_UP) | (1u << B_RIGHT); break;
    case 2: r->btn |= 1u << B_RIGHT; break;
    case 3: r->btn |= (1u << B_RIGHT) | (1u << B_DOWN); break;
    case 4: r->btn |= 1u << B_DOWN; break;
    case 5: r->btn |= (1u << B_DOWN) | (1u << B_LEFT); break;
    case 6: r->btn |= 1u << B_LEFT; break;
    case 7: r->btn |= (1u << B_LEFT) | (1u << B_UP); break;
    }
    if (b & 0x01) { r->btn |= 1u << B_LB; r->phys |= 1u << 4; }
    if (b & 0x02) { r->btn |= 1u << B_RB; r->phys |= 1u << 5; }
    if (b & 0x10) { r->btn |= 1u << B_BACK; r->phys |= 1u << 8; }
    if (b & 0x20) { r->btn |= 1u << B_START; r->phys |= 1u << 9; }
    if (b & 0x40) { r->btn |= 1u << B_L3; r->phys |= 1u << 10; }
    if (b & 0x80) { r->btn |= 1u << B_R3; r->phys |= 1u << 11; }
    r->lt = (lt == 0 && (b & 0x04)) ? 32767 : lt * 257 / 2;
    r->rt = (rt == 0 && (b & 0x08)) ? 32767 : rt * 257 / 2;
    if (c & 0x01) r->phys |= 1u << 12;
    if (c & 0x02) r->phys |= 1u << 13;
    if (c & 0x04) r->phys |= 1u << 14;
    if (c & 0x40) r->phys |= 1u << 15;
    if (c & 0x80) r->phys |= 1u << 16;
}

/* 1 = a frame was produced by the Sony reader. */
static int RawFromSony(int slot, RAW *r)
{
    SonyPadInit();
    if (!diToken[slot][0]) return 0;

    unsigned vid = 0, pid = 0;
    if (sscanf(diToken[slot] + 1, "%x_%x", &vid, &pid) != 2) return 0;
    int kind = SonyKind(vid, pid);
    if (!kind) return 0;

    SONYPAD *sp = &g_sp[slot];
    if (sp->h == INVALID_HANDLE_VALUE)
    {
        if (!SonyOpen(slot, vid, pid, kind)) return 0;
    }

    if (!sp->pending)
    {
        HANDLE ev = sp->ov.hEvent;
        memset(&sp->ov, 0, sizeof sp->ov);
        sp->ov.hEvent = ev;
        DWORD got = 0;
        if (ReadFile(sp->h, sp->buf, sizeof sp->buf, &got, &sp->ov))
        {
            sp->len = (int)got;
        }
        else if (GetLastError() == ERROR_IO_PENDING)
        {
            sp->pending = 1;
            return 0;
        }
        else
        {
            CloseHandle(sp->h);
            sp->h = INVALID_HANDLE_VALUE;
            return 0;
        }
    }
    else
    {
        if (WaitForSingleObject(sp->ov.hEvent, 0) != WAIT_OBJECT_0) return 0;
        DWORD got = 0;
        if (!GetOverlappedResult(sp->h, &sp->ov, &got, FALSE) || got == 0)
        {
            sp->pending = 0;
            return 0;
        }
        sp->pending = 0;
        sp->len = (int)got;
    }

    if (sp->len < 3) return 0;
    RAW frame;
    SonyParse(slot, sp->buf, sp->len, &frame);
    *r = frame;
    return 1;
}

/* ---------------- auto-detection ---------------- */

// Without mapivo.ini the DLLs were copied on their own, so no mapping was
// ever written. Slots then choose a pad automatically: real XInput pads first,
// and a DirectInput pad only when no XInput pad is present (an XInput pad also
// enumerates through DirectInput, so mixing the two would duplicate it).
typedef struct { char tok[8][24]; int count; } AUTOLIST;

static BOOL CALLBACK AutoCb(LPCDIDEVICEINSTANCEW inst, LPVOID ctx)
{
    AUTOLIST *a = (AUTOLIST *)ctx;
    if (a->count >= 8) return DIENUM_STOP;
    DiTokenOf(&inst->guidProduct, a->tok[a->count]);
    for (int i = 0; i < a->count; i++)
        if (strcmp(a->tok[i], a->tok[a->count]) == 0) return DIENUM_CONTINUE;
    a->count++;
    return DIENUM_CONTINUE;
}

static void AutoAssign(void)
{
    static PFN_GETSTATE real;
    if (!real) real = (PFN_GETSTATE)RealProc("XInputGetState");
    if (!real) return;

    int phys[4], n = 0;
    for (int i = 0; i < 4; i++)
    {
        PAD_STATE st;
        if (real((DWORD)i, &st) == 0) phys[n++] = i;
    }

    for (int s = 0; s < 4; s++)
    {
        int wasDi = diToken[s][0] != 0;
        map[s] = -1;
        diToken[s][0] = 0;
        if (s < n) map[s] = phys[s];
        if (wasDi) DropDevice(s);
    }

    if (n > 0) return;

    IDirectInput8W *di = DI();
    if (!di) return;
    AUTOLIST a;
    memset(&a, 0, sizeof a);
    di->lpVtbl->EnumDevices(di, DI8DEVCLASS_GAMECTRL, AutoCb, &a, DIEDFL_ATTACHEDONLY);
    for (int s = 0; s < 4 && s < a.count; s++)
    {
        if (diToken[s][0] && strcmp(diToken[s], a.tok[s]) == 0) continue;
        strcpy(diToken[s], a.tok[s]);
        DropDevice(s);
    }
}

static void AutoMaybe(void)
{
    if (cfgPresent) return;
    unsigned long long now = GetTickCount64();
    if (autoTick && now - autoTick < 2000) return;
    autoTick = now;
    AutoAssign();
}

// An XInput pad that is switched off and on may come back on a different user
// index. Adopt it when it is the only connected pad, so one-pad games keep
// working without touching the profile or the ini.
static unsigned long long g_probe[4];

static int RecoverPhys(int slot, PFN_GETSTATE real)
{
    unsigned long long now = GetTickCount64();
    if (now < g_probe[slot]) return -1;
    g_probe[slot] = now + 1500;

    int only = -1, n = 0;
    for (int i = 0; i < 4; i++)
    {
        PAD_STATE st;
        if (real((DWORD)i, &st) == 0) { n++; only = i; }
    }
    if (n != 1 || only == map[slot]) return -1;

    // Never take a pad another configured slot is already using.
    for (int s = 0; s < 4; s++)
        if (s != slot && !diToken[s][0] && map[s] == only) return -1;

    map[slot] = only;
    Log("xinput pad came back on another index");
    return only;
}

/* ---------------- exports ---------------- */

DWORD WINAPI XInputGetState(DWORD idx, PAD_STATE *s);
DWORD WINAPI XInputGetStateEx(DWORD idx, PAD_STATE *s);

// XInputGetStateEx (ordinal 100 on 1_3/1_4) is the guide-button-aware state.
// The extra buttons live in the high word of wButtons, so returning the mapped
// state unchanged is enough.
DWORD WINAPI XInputGetStateEx(DWORD idx, PAD_STATE *s)
{
    return XInputGetState(idx, s);
}

DWORD WINAPI XInputWaitForGuideButton(DWORD idx, DWORD f, void *p)
{
    (void)idx; (void)f; (void)p;
    return 1167;
}

DWORD WINAPI XInputCancelGuideButtonWait(DWORD idx)
{
    (void)idx;
    return 1167;
}

DWORD WINAPI XInputPowerOffController(DWORD idx)
{
    (void)idx;
    return 1167;
}

DWORD WINAPI XInputGetAudioDeviceIds(DWORD idx, void *out, void *count, void *ids)
{
    (void)idx; (void)out; (void)count; (void)ids;
    return 1167;
}

DWORD WINAPI XInputGetBaseBusInformation(DWORD idx, void *info)
{
    (void)idx; (void)info;
    return 1167;
}

DWORD WINAPI XInputGetState(DWORD idx, PAD_STATE *s)
{
    static PFN_GETSTATE real;
    static unsigned long long tick;
    if (!real) real = (PFN_GETSTATE)RealProc("XInputGetState");
    HMODULE mod = NULL; mod = RealModule(); (void)mod;

    AutoMaybe();

    int di = (idx <= 3 && diToken[idx][0]);
    if (!di)
    {
        int phys = (idx <= 3) ? map[idx] : -1;
        if (phys >= 0 && real)
        {
            DWORD rc = real((DWORD)phys, s);
            if (rc != 0)
            {
                int moved = RecoverPhys((int)idx, real);
                if (moved >= 0) rc = real((DWORD)moved, s);
            }
            if (rc == 0 && mapped[idx] && s)
            {
                RAW r;
                RawFromXInput(s, &r);
                BuildMapped(&r, (int)idx, &s->Gamepad);
            }
            if (rc == 0) return 0;
        }
    }
    if (idx > 3) return 1167;
    if (!s) return 1167;

    // Pick up mapping edits while the game is running; the app is not needed.
    unsigned long long now = GetTickCount64();
    if (now - tick > 1000) { tick = now; WatchConfig(); }

    if (di) {
        RAW r;
        if (!RawFromSony((int)idx, &r) && !RawFromDI((int)idx, &r)) { memset(s, 0, sizeof *s); return 1167; }
        s->dwPacketNumber = GetTickCount();
        BuildMapped(&r, (int)idx, &s->Gamepad);
        return 0;
    }
    memset(s, 0, sizeof *s);
    return 1167;
}

DWORD WINAPI XInputSetState(DWORD idx, PAD_VIBRATION *v)
{
    static PFN_SETSTATE real;
    if (!real) real = (PFN_SETSTATE)RealProc("XInputSetState");
    HMODULE mod = NULL; mod = RealModule(); (void)mod;
    AutoMaybe();

    int di = (idx <= 3 && diToken[idx][0]);
    if (!di)
    {
        int phys = (idx <= 3) ? map[idx] : -1;
        if (phys >= 0 && real && vib[idx])
        {
            if (real((DWORD)phys, v) != 0)
            {
                static PFN_GETSTATE getState;
                if (!getState) getState = (PFN_GETSTATE)RealProc("XInputGetState");
                int moved = getState ? RecoverPhys((int)idx, getState) : -1;
                if (moved >= 0) real((DWORD)moved, v);
            }
        }
    }
    if (idx > 3) return 1167;
    if (!vib[idx]) return 0;
    if (!di) return 0;

    IDirectInputDevice8W *dev = DiDevice((int)idx);
    if (!dev) return 0;

    if (!g_eff[idx] && g_effTried[idx] < 3) {
        g_effTried[idx]++;
        dev->lpVtbl->Acquire(dev);
        DIEFFECT eff;
        memset(&eff, 0, sizeof eff);
        DWORD axes[1] = { (DWORD)g_lay[idx].lx }; // real X axis on this layout
        LONG dir[1] = { 0 };
        DICONSTANTFORCE cf = { 0 };
        eff.dwSize = sizeof eff;
        eff.dwFlags = DIEFF_CARTESIAN | DIEFF_OBJECTOFFSETS;
        eff.dwDuration = INFINITE;
        eff.dwGain = DI_FFNOMINALMAX;
        eff.dwTriggerButton = DIEB_NOTRIGGER;
        eff.cAxes = 1;
        eff.rgdwAxes = axes;
        eff.rglDirection = dir;
        eff.cbTypeSpecificParams = sizeof cf;
        eff.lpvTypeSpecificParams = &cf;
        IDirectInputEffect *created = NULL;
        HRESULT cr = dev->lpVtbl->CreateEffect(dev, &GUID_ConstantForce, &eff, &created, NULL);
        if (SUCCEEDED(cr)) g_eff[idx] = created;
        char msg[64];
        wsprintfA(msg, "vibration effect slot %lu hr %08lX", idx, (unsigned long)cr);
        Log(msg);
    }
    if (!g_eff[idx]) {   // DirectInput could not create an effect: try HID
        HidSet((int)idx, v->wLeftMotorSpeed / 257, v->wRightMotorSpeed / 257, 500);
        return 0;
    }
    if (g_eff[idx]) {
        LONG mag = (v->wLeftMotorSpeed + v->wRightMotorSpeed) / 2;
        DICONSTANTFORCE cf = { mag };
        DIEFFECT eff;
        memset(&eff, 0, sizeof eff);
        eff.dwSize = sizeof eff;
        eff.cbTypeSpecificParams = sizeof cf;
        eff.lpvTypeSpecificParams = &cf;
        if (SUCCEEDED(g_eff[idx]->lpVtbl->SetParameters(g_eff[idx], &eff, DIEP_TYPESPECIFICPARAMS)))
        {
            if (mag > 0) g_eff[idx]->lpVtbl->Start(g_eff[idx], 1, 0);
            else g_eff[idx]->lpVtbl->Stop(g_eff[idx]);
        }
    }
    return 0;
}

DWORD WINAPI XInputGetCapabilities(DWORD idx, DWORD f, void *c)
{
    static PFN_GETCAPS real;
    if (!real) real = (PFN_GETCAPS)RealProc("XInputGetCapabilities");
    HMODULE mod = NULL; mod = RealModule(); (void)mod;
    AutoMaybe();

    int di = (idx <= 3 && diToken[idx][0]);
    if (!di)
    {
        int phys = (idx <= 3) ? map[idx] : -1;
        if (phys >= 0 && real) return real((DWORD)phys, f, c);
    }
    if (idx > 3) return 1167;

    unsigned char *p = (unsigned char *)c;
    if (!p) return 1167;
    memset(p, 0, 20);
    p[0] = 1;          // XINPUT_DEVTYPE_GAMEPAD
    p[1] = 1;          // XINPUT_DEVSUBTYPE_GAMEPAD
    *(WORD *)(p + 2) = 0x0471; // VOICE_SUPPORTED | FFB_SUPPORTED
    return 0;
}

void WINAPI XInputEnable(BOOL enable)
{
    typedef void (WINAPI *F)(BOOL);
    static F f;
    if (!f) f = (F)RealProc("XInputEnable");
    HMODULE mod = NULL; mod = RealModule(); (void)mod;
    if (f) f(enable);
}

DWORD WINAPI XInputGetBatteryInformation(DWORD i, BYTE t, void *b)
{
    static PFN_GENERIC f;
    if (!f) f = (PFN_GENERIC)RealProc("XInputGetBatteryInformation");
    HMODULE mod = NULL; mod = RealModule(); (void)mod;
    if (i > 3) return 1167;
    if (diToken[i][0]) {
        unsigned char *p = (unsigned char *)b;
        if (p) { p[0] = 0x03; p[1] = 0; } // WIRED, FULL
        return 0;
    }
    int phys = map[i];
    return (phys >= 0 && f) ? f((DWORD)phys, t, b) : 1167;
}

DWORD WINAPI XInputGetKeystroke(DWORD i, DWORD f2, void *k)
{
    static PFN_GENERIC f;
    if (!f) f = (PFN_GENERIC)RealProc("XInputGetKeystroke");
    HMODULE mod = NULL; mod = RealModule(); (void)mod;
    if (i > 3) return 1167;
    if (diToken[i][0]) return 1167;
    int phys = map[i];
    return (phys >= 0 && f) ? f((DWORD)phys, f2, k) : 1167;
}

DWORD WINAPI XInputGetDSoundAudioDeviceGuids(DWORD i, void *r, void *c)
{
    typedef DWORD (WINAPI *F)(DWORD, void *, void *);
    static F f;
    if (!f) f = (F)RealProc("XInputGetDSoundAudioDeviceGuids");
    HMODULE mod = NULL; mod = RealModule(); (void)mod;
    if (i > 3) return 1167;
    if (diToken[i][0]) return 1167;
    int phys = map[i];
    return (phys >= 0 && f) ? f((DWORD)phys, r, c) : 1167;
}
