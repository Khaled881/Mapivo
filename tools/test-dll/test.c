#include <windows.h>

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved)
{
    (void)h; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        OutputDebugStringA("Mapivo test DLL attached\n");
    }
    return TRUE;
}

__declspec(dllexport) int MapivoTestAdd(int a, int b)
{
    return a + b;
}

__declspec(dllexport) int MapivoTestBits(void)
{
    return (int)(sizeof(void*) * 8);
}
