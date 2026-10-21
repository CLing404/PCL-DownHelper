// Launcher.cpp

#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <psapi.h>
#include <shlwapi.h>
#include <commdlg.h>
#include <stdio.h>
#include <string>
#include "IniCfg.h"

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "comdlg32.lib")
#pragma comment(lib, "psapi.lib")

struct LogCfg
{
	bool console = true;
	bool file = true;
	int  level = 1;
	bool waitExit = true;
};

static LogCfg      g_log;
static std::wstring g_logPath;
static std::wstring g_dir;

// 读取 ini 中的日志配置
static void LoadLogCfg(const std::wstring& ini)
{
	g_log.console = inicfg::GetBool(ini, L"Log", L"Console", true);
	g_log.file = inicfg::GetBool(ini, L"Log", L"File", true);
	g_log.waitExit = inicfg::GetBool(ini, L"Log", L"WaitExit", true);
	g_log.level = inicfg::GetInt(ini, L"Log", L"Level", 1, 0, 2);
}

// 向标准输出写入 UTF-16 字符串
static void ConsoleOut(const std::wstring& s)
{
	HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
	if (h == INVALID_HANDLE_VALUE || h == nullptr) return;
	DWORD written = 0;
	WriteConsoleW(h, s.c_str(), (DWORD)s.size(), &written, nullptr);
}

// 清空日志文件，使本次运行从空文件开始
static void TruncateLog()
{
	if (g_logPath.empty()) return;
	FILE* f = _wfopen(g_logPath.c_str(), L"wb");
	if (f) fclose(f);
}

// 以 UTF-8 追加写入文本文件
static void FileOut(const std::wstring& s)
{
	if (g_logPath.empty()) return;
	int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(),
		nullptr, 0, nullptr, nullptr);
	if (n <= 0) return;
	std::string u((size_t)n, '\0');
	WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), &u[0], n, nullptr, nullptr);
	FILE* f = _wfopen(g_logPath.c_str(), L"ab");
	if (f) { fwrite(u.data(), 1, u.size(), f); fclose(f); }
}

// 按等级输出带时间戳的日志行
static void LOG(int level, const wchar_t* fmt, ...)
{
	if (level > g_log.level) return;

	wchar_t body[1024];
	va_list ap; va_start(ap, fmt);
	_vsnwprintf_s(body, _countof(body), _TRUNCATE, fmt, ap);
	va_end(ap);

	SYSTEMTIME st; GetLocalTime(&st);
	wchar_t ts[32];
	swprintf_s(ts, L"%02d:%02d:%02d.%03d", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

	const wchar_t* tag = (level == 0) ? L"ERR" : ((level == 1) ? L"INF" : L"DBG");

	wchar_t line[1200];
	swprintf_s(line, L"[%s][%s] %s\n", ts, tag, body);

	if (g_log.console) ConsoleOut(line);
	if (g_log.file) FileOut(line);
}

// 获取本可执行文件所在目录
static std::wstring DirOfSelf()
{
	wchar_t p[MAX_PATH] = { 0 };
	GetModuleFileNameW(nullptr, p, MAX_PATH);
	std::wstring s(p);
	size_t i = s.find_last_of(L"\\/");
	return (i == std::wstring::npos) ? L"." : s.substr(0, i);
}

// 弹出打开文件对话框，返回选中的路径
static bool PickFile(std::wstring& out, const wchar_t* filter,
	const wchar_t* title, const wchar_t* defName)
{
	wchar_t buf[MAX_PATH] = { 0 };
	if (defName) wcscpy_s(buf, defName);   // 预填默认文件名，用户直接确认即可
	OPENFILENAMEW ofn{};
	ofn.lStructSize = sizeof(ofn);
	ofn.lpstrFilter = filter;
	ofn.lpstrFile = buf;
	ofn.nMaxFile = MAX_PATH;
	ofn.lpstrTitle = title;
	ofn.lpstrInitialDir = g_dir.empty() ? nullptr : g_dir.c_str();
	ofn.Flags = OFN_FILEMUSTEXIST | OFN_HIDEREADONLY | OFN_EXPLORER;
	if (!GetOpenFileNameW(&ofn)) return false;
	out = buf;
	return true;
}

// 初始化配置文件，写入含全部键的默认模板
static void EnsureIniTemplate(const std::wstring& ini)
{
	if (PathFileExistsW(ini.c_str())) return;

	const char* text =
		"[PCL]\r\n"
		"; Path: PCL main program\r\n"
		"Path=\r\n"
		"; Bootstrap: native injector DLL\r\n"
		"Bootstrap=\r\n"
		"; Payload: managed patch DLL\r\n"
		"Payload=\r\n"
		"\r\n"
		"[Log]\r\n"
		"; Console: print to console (true / false), default true\r\n"
		"#Console=true\r\n"
		"; File: write launcher.log (true / false), default true\r\n"
		"#File=true\r\n"
		"; Level: 0=error 1=normal 2=verbose, default 1\r\n"
		"#Level=1\r\n"
		"; WaitExit: keep window open until PCL exits (true / false), default true\r\n"
		"#WaitExit=true\r\n";

	FILE* f = _wfopen(ini.c_str(), L"wb");
	if (!f) { LOG(0, L"cannot create Launcher.ini"); return; }
	fwrite(text, 1, strlen(text), f);
	fclose(f);
	LOG(1, L"[+] created Launcher.ini template");
}

// 校验 ini 中是否配置了路径，缺失则弹窗选择并写回
static bool EnsurePath(std::wstring& path, const std::wstring& ini,
	const wchar_t* key, const wchar_t* defName,
	const wchar_t* filter, const wchar_t* title)
{
	if (!path.empty())
	{
		if (PathFileExistsW(path.c_str()))
		{
			LOG(1, L"[=] %s = %s", key, path.c_str());
			return true;
		}
		LOG(1, L"[!] %s not found: %s", key, path.c_str());
	}
	else
	{
		LOG(1, L"[!] %s not set in Launcher.ini", key);
	}

	LOG(1, L"Launcher.ini not found in the current directory, please choose the main program yourself.");

	if (!PickFile(path, filter, title, defName))
	{
		LOG(0, L"[-] cancelled: %s", key);
		return false;
	}

	std::wstring quoted = L"\"" + path + L"\"";
	WritePrivateProfileStringW(L"PCL", key, quoted.c_str(), ini.c_str());
	LOG(1, L"[+] saved to Launcher.ini: %s = %s", key, path.c_str());
	return true;
}

// 轮询等待目标进程加载指定模块
static bool WaitForClr(HANDLE hProc, int timeoutMs)
{
	int waited = 0;
	while (waited < timeoutMs)
	{
		HMODULE mods[1024];
		DWORD need = 0;
		if (EnumProcessModules(hProc, mods, sizeof(mods), &need))
		{
			DWORD n = need / sizeof(HMODULE);
			for (DWORD i = 0; i < n && i < 1024; i++)
			{
				wchar_t name[MAX_PATH] = { 0 };
				if (!GetModuleBaseNameW(hProc, mods[i], name, MAX_PATH)) continue;
				if (_wcsicmp(name, L"mscoree.dll") == 0)
				{
					LOG(2, L"检测到 mscoree.dll（CLR 就绪），已等待 %d ms", waited);
					Sleep(1250);
					return true;
				}
			}
		}
		Sleep(200);
		waited += 200;
	}
	LOG(0, L"超时未见到 mscoree.dll");
	return false;
}

// 在目标进程中通过远程线程加载指定 DLL
static bool InjectDll(HANDLE hProc, const std::wstring& dll)
{
	SIZE_T size = (dll.size() + 1) * sizeof(wchar_t);
	void* remote = VirtualAllocEx(hProc, nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
	if (!remote) { LOG(0, L"VirtualAllocEx 失败 gle=%lu", GetLastError()); return false; }

	if (!WriteProcessMemory(hProc, remote, dll.c_str(), size, nullptr))
	{
		LOG(0, L"WriteProcessMemory 失败 gle=%lu", GetLastError());
		VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
		return false;
	}

	HMODULE hKernel32 = GetModuleHandleW(L"kernel32.dll");
	if (!hKernel32)
	{
		LOG(0, L"GetModuleHandleW(kernel32.dll) 失败 gle=%lu", GetLastError());
		VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
		return false;
	}

	auto pLoadLibraryW = reinterpret_cast<LPTHREAD_START_ROUTINE>(
		GetProcAddress(hKernel32, "LoadLibraryW"));
	if (!pLoadLibraryW)
	{
		LOG(0, L"GetProcAddress(LoadLibraryW) 失败 gle=%lu", GetLastError());
		VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
		return false;
	}

	HANDLE hThread = CreateRemoteThread(hProc, nullptr, 0, pLoadLibraryW, remote, 0, nullptr);
	if (!hThread)
	{
		LOG(0, L"CreateRemoteThread 失败 gle=%lu", GetLastError());
		VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
		return false;
	}

	DWORD waitRet = WaitForSingleObject(hThread, 15000);
	DWORD exit = 0;
	if (waitRet == WAIT_OBJECT_0)
	{
		GetExitCodeThread(hThread, &exit);
	}
	else
	{
		LOG(0, L"等待注入线程超时 waitRet=%lu", waitRet);
	}
	CloseHandle(hThread);
	VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);

	if (exit == 0) { LOG(0, L"目标进程内 LoadLibraryW 返回 NULL，或线程未完成"); return false; }
	LOG(1, L"注入成功,Bootstrap.dll 已注入");
	return true;
}

// 程序入口：读取配置、启动目标进程、注入并等待其退出
int wmain()
{
	g_dir = DirOfSelf();
	std::wstring ini = g_dir + L"\\Launcher.ini";
	bool iniExists = PathFileExistsW(ini.c_str());

	EnsureIniTemplate(ini);

	LoadLogCfg(ini);
	g_logPath = g_dir + L"\\launcher.log";
	TruncateLog();

	LOG(2, L"配置：Console=%s File=%s Level=%d WaitExit=%s",
		g_log.console ? L"true" : L"false",
		g_log.file ? L"true" : L"false",
		g_log.level,
		g_log.waitExit ? L"true" : L"false");

	if (!iniExists)
		LOG(1, L"Launcher.ini not found in the current directory, please choose the main program yourself.");

	std::wstring pclPath = inicfg::GetStr(ini, L"PCL", L"Path");
	if (!EnsurePath(pclPath, ini, L"Path", L"Plain Craft Launcher 2.exe",
		L"PCL2 Release/preview\0*Plain Craft Launcher 2.exe\0"
		L"Executable (*.exe)\0*.exe\0"
		L"All files (*.*)\0*.*\0",
		L"Select PCL main program"))
		return 1;

	std::wstring dllPath = inicfg::GetStr(ini, L"PCL", L"Bootstrap");
	if (!EnsurePath(dllPath, ini, L"Bootstrap", L"Bootstrap.dll",
		L"Bootstrap DLL\0*Bootstrap*.dll\0"
		L"DLL files (*.dll)\0*.dll\0"
		L"All files (*.*)\0*.*\0",
		L"Select Bootstrap.dll (native injector)"))
		return 1;

	std::wstring payPath = inicfg::GetStr(ini, L"PCL", L"Payload");
	if (!EnsurePath(payPath, ini, L"Payload", L"Payload.dll",
		L"Payload DLL\0*Payload*.dll\0"
		L"DLL files (*.dll)\0*.dll\0"
		L"All files (*.*)\0*.*\0",
		L"Select Payload.dll (managed patch)"))
		return 1;

	std::wstring workDir = pclPath;
	size_t i = workDir.find_last_of(L"\\/");
	workDir = (i == std::wstring::npos) ? L"" : workDir.substr(0, i);

	STARTUPINFOW si{}; si.cb = sizeof(si);
	PROCESS_INFORMATION pi{};

	if (!CreateProcessW(pclPath.c_str(), nullptr, nullptr, nullptr, FALSE,
		0, nullptr,
		workDir.empty() ? nullptr : workDir.c_str(),
		&si, &pi))
	{
		LOG(0, L"CreateProcess 失败 gle=%lu", GetLastError());
		return 1;
	}

	LOG(1, L"PCL 已启动 pid=%lu，等待 CLR 就绪…", pi.dwProcessId);

	bool ok = WaitForClr(pi.hProcess, 90000);
	if (ok) ok = InjectDll(pi.hProcess, dllPath);

	CloseHandle(pi.hThread);

	if (!ok)
	{
		LOG(0, L"注入失败，请检查权限");
		CloseHandle(pi.hProcess);
		return 1;
	}

	if (!g_log.waitExit)
	{
		LOG(1, L"完成");
		CloseHandle(pi.hProcess);
		return 0;
	}

	LOG(1, L"注入完成");

	for (;;)
	{
		DWORD r = WaitForSingleObject(pi.hProcess, 1000);
		if (r == WAIT_OBJECT_0) break;
		if (r == WAIT_FAILED)
		{
			LOG(0, L"WaitForSingleObject 失败 gle=%lu", GetLastError());
			break;
		}
	}

	DWORD code = 0;
	GetExitCodeProcess(pi.hProcess, &code);
	LOG(1, L"PCL 已退出，退出码=%lu", code);

	CloseHandle(pi.hProcess);
	return 0;
}
