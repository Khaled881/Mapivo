#include <windows.h>
#include <string.h>

typedef HRESULT (WINAPI *CREATE8)(HINSTANCE, DWORD, REFIID, LPVOID*, LPUNKNOWN);
typedef BOOL (WINAPI *EI8CB)(const void*, void*);
typedef HRESULT (STDMETHODCALLTYPE *ENUMFN)(void*, DWORD, EI8CB, void*, DWORD);

static HMODULE RealModule(void)
{
    static HMODULE m;
    if (!m)
    {
        char sys[MAX_PATH], path[MAX_PATH];
        if (GetSystemDirectoryA(sys, MAX_PATH))
        {
            wsprintfA(path, "%s\\dinput8.dll", sys);
            m = LoadLibraryExA(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
        }
    }
    return m;
}

// Pads are served to the game as virtual XInput controllers only, so the real
// ones must not also show up through DirectInput (FIFA listed them twice).
//
// Hiding is only safe inside the exe the profile was installed for. The same
// dinput8.dll sits in the folder for every exe there, so settings.exe and
// launchers would otherwise find zero controllers and crash. mapivo.ini names
// the intended exe with a "host=" line (written by the app); any other process
// passes through untouched. An ini without host= keeps the old hide-everywhere
// behavior for backward compatibility.
static int HideEnabled(void)
{
    static int done, hide;
    if (!done)
    {
        done = 1;
        char exe[MAX_PATH], *slash;
        if (GetModuleFileNameA(NULL, exe, MAX_PATH) &&
            (slash = strrchr(exe, '\\')) != NULL)
        {
            char hostFile[MAX_PATH];
            lstrcpynA(hostFile, slash + 1, sizeof hostFile);
            wsprintfA(slash, "\\mapivo.ini");
            if (GetFileAttributesA(exe) == INVALID_FILE_ATTRIBUTES) return 0;

            // Read the host= line; empty means "hide in every process".
            HANDLE h = CreateFileA(exe, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
            char host[MAX_PATH] = { 0 };
            if (h != INVALID_HANDLE_VALUE)
            {
                char buf[4096] = { 0 };
                DWORD rd = 0;
                ReadFile(h, buf, sizeof(buf) - 1, &rd, NULL);
                CloseHandle(h);
                char *line = buf;
                while (line && *line)
                {
                    char *next = strchr(line, '\n');
                    if (next) *next++ = 0;
                    if (strncmp(line, "host=", 5) == 0)
                    {
                        char *v = line + 5;
                        while (*v == ' ' || *v == '\t') v++;
                        lstrcpynA(host, v, sizeof host);
                        char *e = host + lstrlenA(host);
                        while (e > host && (e[-1] == '\r' || e[-1] == ' ' || e[-1] == '\t')) *--e = 0;
                        break;
                    }
                    line = next;
                }
            }
            if (!host[0]) hide = 1;                    // legacy ini: hide everywhere
            else hide = _stricmp(host, hostFile) == 0;  // only in the chosen exe
        }
    }
    return hide;
}

static EI8CB g_userCb;
static void *g_userRef;
static void *g_origVtbl[16];
static void *g_newVtbl[16];

// DIDEVICEINSTANCE.dwDevType: bits 16..23 hold the device class; 4 = GAMECTRL.
// Only game controllers are ever touched: keyboards and mice (classes 1 and 2)
// fall through to the user's callback, so a game keeps its normal keyboard.
static BOOL WINAPI FilterCb(const void *ddi, void *pvRef)
{
    (void)pvRef;
    if (!g_userCb) return TRUE;
    if (ddi && ((((const DWORD *)ddi)[5] >> 16) & 0xff) == 4) return FALSE;
    return g_userCb(ddi, g_userRef);
}

static HRESULT STDMETHODCALLTYPE MyEnumDevices(void *self, DWORD dwDevType,
                                               EI8CB cb, void *pvRef, DWORD dwFlags)
{
    ENUMFN orig = (ENUMFN)g_origVtbl[4];
    if (!cb) return orig(self, dwDevType, cb, pvRef, dwFlags);
    g_userCb = cb;
    g_userRef = pvRef;
    return orig(self, dwDevType, FilterCb, pvRef, dwFlags);
}

HRESULT WINAPI DirectInput8Create(HINSTANCE hinst, DWORD version, REFIID iid,
                                  LPVOID *out, LPUNKNOWN unk)
{
    CREATE8 f = NULL;
    HMODULE m = RealModule();
    if (m) f = (CREATE8)GetProcAddress(m, "DirectInput8Create");
    if (!f || !out) { if (out) *out = NULL; return 0x8007007E; }

    HRESULT hr = f(hinst, version, iid, out, unk);
    if (FAILED(hr) || !*out) return hr;

    if (HideEnabled())
    {
        void **vt = *(void***)out;
        if (vt[4] != (void *)MyEnumDevices)
        {
            memcpy(g_origVtbl, vt, sizeof g_origVtbl);
            memcpy(g_newVtbl, vt, sizeof g_newVtbl);
            g_newVtbl[4] = (void *)MyEnumDevices;
            *(void***)out = g_newVtbl;
        }
    }
    return hr;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void **out)
{ (void)clsid; (void)iid; if (out) *out = NULL; return 0x80040111; }

STDAPI DllCanUnloadNow(void) { return 1; }

STDAPI DllRegisterServer(void) { return 0; }

STDAPI DllUnregisterServer(void) { return 0; }

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{ (void)inst; (void)reason; (void)reserved; return TRUE; }
