// Bootstrap.dll —— 在 PCL 进程内把 PclPatch.dll 送进默认 AppDomain
//
// 破坏性声明：PCL.exe 字节零改动；不写注册表；不改系统环境变量
// 编译：VS2022 / C++ / DLL / x64，链接 mscoree.lib
//
// 关键点（loader lock）：
//   DllMain 里绝不能 LoadLibrary / 不能碰 CLR。
//   只做 DisableThreadLibraryCalls + CreateThread，真正的活交给工作线程。

#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS   // 关掉 _wfopen 等 C4996 弃用警告（VS 默认当错误）
#include <windows.h>
#include <metahost.h>
#include <stdio.h>
#include <stdarg.h>
#include <string>
#pragma comment(lib, "mscoree.lib")

static std::wstring g_dir;
static CRITICAL_SECTION g_cs;

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
}

static void BLog(const char* fmt, ...)
{
	char buf[1024];
	va_list ap; va_start(ap, fmt);
	vsnprintf(buf, sizeof(buf), fmt, ap);
	va_end(ap);

	EnterCriticalSection(&g_cs);
	std::wstring path = g_dir + L"\\bootstrap.log";
	FILE* f = _wfopen(path.c_str(), L"a");
	if (f) { fprintf(f, "%s\n", buf); fclose(f); }
	LeaveCriticalSection(&g_cs);
}

typedef HRESULT(STDAPICALLTYPE* FnCLRCreateInstance)(REFCLSID, REFIID, LPVOID*);

static DWORD WINAPI Worker(LPVOID)
{
	InitDir();
	BLog("=== worker start (pid=%lu) ===", GetCurrentProcessId());

	wchar_t payload[MAX_PATH] = { 0 };
	std::wstring ini = g_dir + L"\\Launcher.ini";
	GetPrivateProfileStringW(L"PCL", L"Payload", L"", payload, MAX_PATH, ini.c_str());
	std::wstring pay(payload);
	if (pay.empty()) pay = g_dir + L"\\PclPatch.dll";
	BLog("payload = %ls", pay.c_str());

	// Launcher 已确认 mscoree.dll 在模块列表里才注入，这里直接取句柄即可
	// 不再主动 LoadLibrary —— 注入时进程早已完成初始化
	HMODULE mscoree = GetModuleHandleW(L"mscoree.dll");
	if (!mscoree) { BLog("no mscoree.dll in this process"); return 1; }
	BLog("mscoree ready");

	// 2) 拿 CLR 宿主接口
	auto fn = reinterpret_cast<FnCLRCreateInstance>(
		GetProcAddress(mscoree, "CLRCreateInstance"));
	if (!fn) { BLog("no CLRCreateInstance"); return 1; }

	ICLRMetaHost* mh = nullptr;
	HRESULT hr = fn(CLSID_CLRMetaHost, IID_ICLRMetaHost, reinterpret_cast<LPVOID*>(&mh));
	if (FAILED(hr)) { BLog("CLRCreateInstance 0x%08lX", hr); return 1; }

	ICLRRuntimeInfo* ri = nullptr;
	hr = mh->GetRuntime(L"v4.0.30319", IID_ICLRRuntimeInfo, reinterpret_cast<LPVOID*>(&ri));
	if (FAILED(hr)) { BLog("GetRuntime 0x%08lX", hr); return 1; }

	ICLRRuntimeHost* host = nullptr;
	hr = ri->GetInterface(CLSID_CLRRuntimeHost, IID_ICLRRuntimeHost,
		reinterpret_cast<LPVOID*>(&host));
	if (FAILED(hr)) { BLog("GetInterface 0x%08lX", hr); return 1; }

	// 3) 确保 CLR 已启动（已启动时返回非 S_OK，忽略即可）
	hr = host->Start();
	BLog("CLR Start hr=0x%08lX", hr);

	// 4) 把托管 payload 载进默认 AppDomain
	//    默认域可能还没建好，失败就重试
	for (int i = 0; i < 400; ++i)
	{
		DWORD ret = 0;
		hr = host->ExecuteInDefaultAppDomain(pay.c_str(),
			L"PclPatch.Entry", L"Init", L"", &ret);
		if (SUCCEEDED(hr)) { BLog("payload loaded, Init returned %lu", ret); break; }
		Sleep(100);
	}
	if (FAILED(hr)) BLog("ExecuteInDefaultAppDomain 最终失败 0x%08lX", hr);

	if (host) host->Release();
	if (ri) ri->Release();
	if (mh) mh->Release();
	return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_ATTACH)
	{
		InitializeCriticalSection(&g_cs);
		DisableThreadLibraryCalls(hModule);
		// 注意：这里只开线程，绝不 LoadLibrary / 不碰 CLR
		HANDLE t = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
		if (t) CloseHandle(t);
	}
	else if (reason == DLL_PROCESS_DETACH)
	{
		DeleteCriticalSection(&g_cs);
	}
	return TRUE;
}
