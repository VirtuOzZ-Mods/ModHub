// Mod Hub by VirtuOzZ — portable build launcher.
// The folder root holds this launcher, README RU/EN.txt, Settings.json and Languages; the app and its libraries live in the Program
// folder (the bin folder in builds before the layout was renamed).
// The launcher forwards its arguments (for example, an .oiv dropped onto the shortcut) and exits immediately.
#include <windows.h>
#include <string>

static std::wstring ModuleDir() {
    std::wstring path(MAX_PATH, L'\0');
    for (;;) {
        DWORD n = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (n == 0) return {};
        if (n < path.size()) { path.resize(n); break; }
        path.resize(path.size() * 2);
    }
    return path.substr(0, path.find_last_of(L"\\/"));
}

// Arguments as-is: everything after the launcher's own name on the command line.
static const wchar_t* ArgsTail(const wchar_t* cmd) {
    bool quoted = false;
    for (; *cmd; ++cmd) {
        if (*cmd == L'"') quoted = !quoted;
        else if (!quoted && (*cmd == L' ' || *cmd == L'\t')) break;
    }
    while (*cmd == L' ' || *cmd == L'\t') ++cmd;
    return cmd;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    const std::wstring dir = ModuleDir();
    std::wstring bin = dir + L"\\Program";
    std::wstring exe = bin + L"\\ModHub.exe";
    if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) {
        bin = dir + L"\\bin";
        exe = bin + L"\\ModHub.exe";
    }
    if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) {
        MessageBoxW(nullptr, L"Не найдена папка Program с файлами программы.\nРаспакуйте архив целиком и запустите Mod Hub.exe снова.",
                    L"Mod Hub by VirtuOzZ", MB_OK | MB_ICONWARNING);
        return 1;
    }
    std::wstring cmd = L"\"" + exe + L"\"";
    if (const wchar_t* tail = ArgsTail(GetCommandLineW()); *tail) { cmd += L' '; cmd += tail; }
    STARTUPINFOW si{ sizeof(si) };
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, bin.c_str(), &si, &pi)) {
        MessageBoxW(nullptr, L"Не удалось запустить Program\\ModHub.exe.", L"Mod Hub by VirtuOzZ", MB_OK | MB_ICONERROR);
        return 1;
    }
    AllowSetForegroundWindow(pi.dwProcessId);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
