// Bootstrap.cpp

#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <metahost.h>
#include <stdio.h>
#include <stdarg.h>
#include <string>
#include "IniCfg.h"

#pragma comment(lib, "mscoree.lib")

static std::wstring g_dir;
static std::wstring g_ini;
static std::wstring g_logPath;
static CRITICAL_SECTION g_cs;
static int  g_level = 1;
static bool g_fileLog = true;

// 清空日志文件，使本次运行从空文件开始
static void TruncateLog()
{
	if (g_logPath.empty()) return;
	FILE* f = _wfopen(g_logPath.c_str(), L"wb");
	if (f) fclose(f);
}

// 初始化目录路径与日志配置
static void InitDir()
{
	HMODULE h = nullptr;
	GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
		reinterpret_cast<LPCWSTR>(&InitDir), &h);
	wchar_t p[MAX_PATH] = { 0 };
	GetModuleFileNameW(h, p, MAX_PATH);
	std::wstring s(p);
	size_t i = s.find_last_of(L"\\/");
	g_dir = (i == std::wstring::npos) ? L"." : s.substr(0, i);

	g_ini = g_dir + L"\\Launcher.ini";

	g_level = inicfg::GetInt(g_ini, L"Log", L"Level", 1, 0, 2);
	g_fileLog = inicfg::GetBool(g_ini, L"Log", L"File", true);

	g_logPath = g_dir + L"\\bootstrap.log";
	TruncateLog();
}

// 按等级输出带时间戳的日志行
static void BLog(int level, const wchar_t* fmt, ...)
{
	if (level > g_level) return;
	if (!g_fileLog) return;

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

	EnterCriticalSection(&g_cs);
	{
		int n = WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
		if (n > 1)
		{
			std::string u((size_t)n - 1, '\0');
			WideCharToMultiByte(CP_UTF8, 0, line, -1, &u[0], n, nullptr, nullptr);
			FILE* f = _wfopen(g_logPath.c_str(), L"ab");
			if (f) { fwrite(u.data(), 1, u.size(), f); fclose(f); }
		}
	}
	LeaveCriticalSection(&g_cs);
}

typedef HRESULT(STDAPICALLTYPE* FnCLRCreateInstance)(REFCLSID, REFIID, LPVOID*);

// 工作线程：将托管程序集载入默认应用程序域并调用其入口方法
static DWORD WINAPI Worker(LPVOID)
{
	InitDir();
	BLog(1, L"=== worker start pid=%lu ===", GetCurrentProcessId());

	std::wstring pay = inicfg::GetStr(g_ini, L"PCL", L"Payload");
	if (pay.empty()) pay = g_dir + L"\\Payload.dll";
	BLog(2, L"payload = %s", pay.c_str());

	HMODULE mscoree = GetModuleHandleW(L"mscoree.dll");
	if (!mscoree) { BLog(0, L"本进程没有 mscoree.dll，退出"); return 1; }
	BLog(2, L"mscoree 已就绪");

	auto fn = reinterpret_cast<FnCLRCreateInstance>(
		GetProcAddress(mscoree, "CLRCreateInstance"));
	if (!fn) { BLog(0, L"找不到 CLRCreateInstance"); return 1; }

	ICLRMetaHost* mh = nullptr;
	HRESULT hr = fn(CLSID_CLRMetaHost, IID_ICLRMetaHost, reinterpret_cast<LPVOID*>(&mh));
	if (FAILED(hr)) { BLog(0, L"CLRCreateInstance 失败 0x%08lX", (unsigned long)hr); return 1; }

	ICLRRuntimeInfo* ri = nullptr;
	hr = mh->GetRuntime(L"v4.0.30319", IID_ICLRRuntimeInfo, reinterpret_cast<LPVOID*>(&ri));
	if (FAILED(hr))
	{
		BLog(0, L"GetRuntime 失败 0x%08lX", (unsigned long)hr);
		mh->Release();
		return 1;
	}

	ICLRRuntimeHost* host = nullptr;
	hr = ri->GetInterface(CLSID_CLRRuntimeHost, IID_ICLRRuntimeHost,
		reinterpret_cast<LPVOID*>(&host));
	if (FAILED(hr))
	{
		BLog(0, L"GetInterface 失败 0x%08lX", (unsigned long)hr);
		ri->Release(); mh->Release();
		return 1;
	}

	hr = host->Start();
	BLog(2, L"CLR Start hr=0x%08lX", (unsigned long)hr);

	DWORD ret = 0;
	const int kFast = 20, kSlow = 20;
	int attempt = 0;
	for (;;)
	{
		hr = host->ExecuteInDefaultAppDomain(pay.c_str(),
			L"PclPatch.Entry", L"Init", L"", &ret);
		if (SUCCEEDED(hr)) break;

		attempt++;
		if (attempt <= kFast) { Sleep(50); continue; }
		if (attempt <= kFast + kSlow) { Sleep(250); continue; }

		BLog(0, L"ExecuteInDefaultAppDomain 最终失败 0x%08lX（重试 %d 次）",
			(unsigned long)hr, attempt);
		break;
	}

	if (SUCCEEDED(hr))
		BLog(1, L"payload 已加载，Init 返回 %lu（重试 %d 次）", ret, attempt);
	else
		BLog(1, L"payload 加载失败 pid=%lu", GetCurrentProcessId());

	if (host) host->Release();
	if (ri) ri->Release();
	if (mh) mh->Release();
	return SUCCEEDED(hr) ? 0 : 1;
}

// DLL 入口点
BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_ATTACH)
	{
		InitializeCriticalSection(&g_cs);
		DisableThreadLibraryCalls(hModule);
		HANDLE t = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
		if (t) CloseHandle(t);
	}
	else if (reason == DLL_PROCESS_DETACH)
	{
		DeleteCriticalSection(&g_cs);
	}
	return TRUE;
}
