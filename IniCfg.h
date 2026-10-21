

#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <wctype.h>
#include <stdlib.h>

namespace inicfg {

	inline std::wstring Trim(const std::wstring& s)
	{
		size_t a = 0, b = s.size();
		while (a < b && (s[a] == L' ' || s[a] == L'\t' || s[a] == L'\r' || s[a] == L'\n')) a++;
		while (b > a && (s[b - 1] == L' ' || s[b - 1] == L'\t' || s[b - 1] == L'\r' || s[b - 1] == L'\n')) b--;
		return s.substr(a, b - a);
	}

	// 去掉行尾注释：只截断「引号外」的 ; 或 #
	inline std::wstring StripComment(const std::wstring& s)
	{
		bool inQ = false;
		wchar_t q = 0;
		for (size_t i = 0; i < s.size(); i++)
		{
			wchar_t c = s[i];
			if (inQ) { if (c == q) inQ = false; continue; }
			if (c == L'"' || c == L'\'') { inQ = true; q = c; continue; }
			if (c == L';' || c == L'#') return s.substr(0, i);
		}
		return s;
	}

	// 去掉配对的外层引号
	inline std::wstring Unquote(std::wstring s)
	{
		s = Trim(s);
		if (s.size() >= 2)
		{
			wchar_t a = s.front(), b = s.back();
			if ((a == L'"' && b == L'"') || (a == L'\'' && b == L'\''))
				return s.substr(1, s.size() - 2);
		}
		return s;
	}

	// 读字符串：自动去注释 + 去引号 + 去空白
	inline std::wstring GetStr(const std::wstring& ini, const wchar_t* sec,
		const wchar_t* key, const wchar_t* def = L"")
	{
		wchar_t buf[1024] = { 0 };
		GetPrivateProfileStringW(sec, key, def, buf, 1024, ini.c_str());
		std::wstring v = Unquote(StripComment(buf));
		return v.empty() ? L"" : v;
	}

	inline std::wstring Lower(std::wstring s)
	{
		for (size_t i = 0; i < s.size(); i++) s[i] = (wchar_t)::towlower(s[i]);
		return s;
	}

	// 读布尔：true/false（推荐），兼容 1/0、yes/no、on/off
	inline bool GetBool(const std::wstring& ini, const wchar_t* sec,
		const wchar_t* key, bool def)
	{
		std::wstring v = Lower(GetStr(ini, sec, key));
		if (v.empty()) return def;

		if (v == L"true" || v == L"1" || v == L"yes" || v == L"on" ||
			v == L"enable" || v == L"enabled") return true;
		if (v == L"false" || v == L"0" || v == L"no" || v == L"off" ||
			v == L"disable" || v == L"disabled") return false;

		return def;   // 无法识别 -> 用默认值
	}

	inline bool IsInt(const std::wstring& s)
	{
		size_t i = 0;
		if (!s.empty() && (s[0] == L'-' || s[0] == L'+')) i = 1;
		if (i >= s.size()) return false;
		for (; i < s.size(); i++) if (!::iswdigit(s[i])) return false;
		return true;
	}

	// 读整数并夹取到 [lo, hi]
	inline int GetInt(const std::wstring& ini, const wchar_t* sec,
		const wchar_t* key, int def, int lo, int hi)
	{
		std::wstring v = GetStr(ini, sec, key);
		if (v.empty() || !IsInt(v)) return def;
		int n = ::_wtoi(v.c_str());
		if (n < lo) n = lo;
		if (n > hi) n = hi;
		return n;
	}

} // namespace inicfg
