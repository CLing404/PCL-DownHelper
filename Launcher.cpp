// Launcher.cpp —— 启动 PCL，等 CLR 就绪后把 Bootstrap.dll 注入其进程
//
// 破坏性声明：
//   · PCL.exe 磁盘字节：零改动（只改进程内存中的模块列表）
//   · 注册表：不写
//   · 系统环境变量：不改
//   · 唯一痕迹：启动器同目录下多一个 Launcher.ini（记 PCL 路径）
//
// 关键修正（v2）：
//   不再用 CREATE_SUSPENDED 挂起进程后立刻注入！
//   挂起状态下 LdrInitializeThunk 尚未跑完，此时 CreateRemoteThread 执行
//   LoadLibraryW 会触发 0xc0000005 "Exception Processing Message"。
//   改为：正常启动 -> 轮询等 mscoree.dll 出现（CLR 已就绪）-> 再注入。
//
// 编译：VS2022 / C++ / 控制台 / x64
// UAC：项目属性 → 链接器 → 清单文件 → UAC 执行级别 = requireAdministrator

#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <psapi.h>
#include <shlwapi.h>
#include <commdlg.h>
#include <stdio.h>
#include <string>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "comdlg32.lib")
#pragma comment(lib, "psapi.lib")

static std::wstring DirOfSelf()
{
	wchar_t p[MAX_PATH] = { 0 };
	GetModuleFileNameW(nullptr, p, MAX_PATH);
	std::wstring s(p);
	size_t i = s.find_last_of(L"\\/");
	return (i == std::wstring::npos) ? L"." : s.substr(0, i);
}

static bool PickFile(std::wstring& out, const wchar_t* filter, const wchar_t* title)
{
	wchar_t buf[MAX_PATH] = { 0 };
	OPENFILENAMEW ofn{};
	ofn.lStructSize = sizeof(ofn);
	ofn.lpstrFilter = filter;
	ofn.lpstrFile = buf;
	ofn.nMaxFile = MAX_PATH;
	ofn.lpstrTitle = title;
	ofn.Flags = OFN_FILEMUSTEXIST | OFN_HIDEREADONLY;
	if (!GetOpenFileNameW(&ofn)) return false;
	out = buf;
	return true;
}

// 等目标进程把 mscoree.dll 加载进来 —— 说明 .NET 运行时已就绪、进程初始化早已完成
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
					wprintf(L"[*] 检测到 mscoree.dll（CLR 已就绪），等待 %d ms\n", waited);
					// 再多给一点缓冲，避开启动高峰
					Sleep(1500);
					return true;
				}
			}
		}
		Sleep(200);
		waited += 200;
	}
	wprintf(L"[x] 超时未见到 mscoree.dll，PCL 可能不是 .NET 程序或未启动\n");
	return false;
}

// 远程 LoadLibraryW —— 此时进程已完全初始化，安全
static bool InjectDll(HANDLE hProc, const std::wstring& dll)
{
	SIZE_T size = (dll.size() + 1) * sizeof(wchar_t);
	void* remote = VirtualAllocEx(hProc, nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
	if (!remote) { wprintf(L"[x] VirtualAllocEx 失败 gle=%lu\n", GetLastError()); return false; }

	if (!WriteProcessMemory(hProc, remote, dll.c_str(), size, nullptr))
	{
		wprintf(L"[x] WriteProcessMemory 失败 gle=%lu\n", GetLastError());
		VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
		return false;
	}

	auto pLoadLibraryW = reinterpret_cast<LPTHREAD_START_ROUTINE>(
		GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));

	HANDLE hThread = CreateRemoteThread(hProc, nullptr, 0, pLoadLibraryW, remote, 0, nullptr);
	if (!hThread) { wprintf(L"[x] CreateRemoteThread 失败 gle=%lu\n", GetLastError()); return false; }

	WaitForSingleObject(hThread, 15000);
	DWORD exit = 0;
	GetExitCodeThread(hThread, &exit);
	CloseHandle(hThread);
	VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
	if (exit == 0) wprintf(L"[x] 目标进程内 LoadLibrary 返回 NULL\n");
	else wprintf(L"[*] 注入成功\n");
	return exit != 0;
}

int wmain()
{
	std::wstring dir = DirOfSelf();
	std::wstring ini = dir + L"\\Launcher.ini";

	wchar_t pcl[MAX_PATH] = { 0 }, dll[MAX_PATH] = { 0 };
	GetPrivateProfileStringW(L"PCL", L"Path", L"", pcl, MAX_PATH, ini.c_str());
	GetPrivateProfileStringW(L"PCL", L"Bootstrap", L"", dll, MAX_PATH, ini.c_str());

	std::wstring pclPath(pcl), dllPath(dll);
	if (dllPath.empty()) dllPath = dir + L"\\Bootstrap.dll";

	if (pclPath.empty() || !PathFileExistsW(pclPath.c_str()))
	{
		wprintf(L"[?] 未配置 PCL 路径，请选择 Plain Craft Launcher 2.exe\n");
		if (!PickFile(pclPath,
			L"PCL\0Plain Craft Launcher 2.exe\0可执行文件\0*.exe\0",
			L"选择 PCL 主程序"))
		{
			wprintf(L"[x] 已取消\n");
			return 1;
		}
		WritePrivateProfileStringW(L"PCL", L"Path", pclPath.c_str(), ini.c_str());
	}

	if (!PathFileExistsW(dllPath.c_str()))
	{
		wprintf(L"[x] 找不到 Bootstrap.dll：%s\n", dllPath.c_str());
		return 1;
	}

	std::wstring workDir = pclPath;
	size_t i = workDir.find_last_of(L"\\/");
	workDir = (i == std::wstring::npos) ? L"" : workDir.substr(0, i);

	STARTUPINFOW si{}; si.cb = sizeof(si);
	PROCESS_INFORMATION pi{};

	// 注意：不再 CREATE_SUSPENDED，让 PCL 正常跑起来
	if (!CreateProcessW(pclPath.c_str(), nullptr, nullptr, nullptr, FALSE,
		0, nullptr,
		workDir.empty() ? nullptr : workDir.c_str(),
		&si, &pi))
	{
		wprintf(L"[x] CreateProcess 失败 gle=%lu\n", GetLastError());
		return 1;
	}

	wprintf(L"[*] PCL 已启动 pid=%lu，等待 CLR 就绪…\n", pi.dwProcessId);

	bool ok = WaitForClr(pi.hProcess, 90000);
	if (ok) ok = InjectDll(pi.hProcess, dllPath);

	CloseHandle(pi.hThread);
	CloseHandle(pi.hProcess);
	wprintf(L"[*] %s\n", ok ? L"完成，PCL 应交由它自己继续运行" : L"失败");
	return ok ? 0 : 1;
}
