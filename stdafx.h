#pragma once

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <winsock2.h>
#include <windows.h>
#include <mmsystem.h>
#include <shlobj.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <memory>
#include <algorithm>
#include <stdexcept>
#include <cctype>
#include <cstdio>
#include <sstream>
#include <functional>
#include <commctrl.h>

#include <foobar2000/SDK/foobar2000.h>
#include <foobar2000/helpers/helpers.h>
#include <foobar2000/helpers/foobar2000+atl.h>

#include "resource.h"
#include "lx_common.h"
