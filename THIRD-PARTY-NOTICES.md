# Third-Party Notices

DisplayProfiles is licensed under the MIT License (see [LICENSE](LICENSE)). It uses or references the
following third-party material.

## NVIDIA NVAPI

DisplayProfiles does not ship NVAPI code or headers. It calls `nvapi64.dll`, which is installed with
the NVIDIA display driver, through `nvapi_QueryInterface`. The function IDs and structure layouts in
`src/DisplayProfiles.Core/NvApi.cs` were taken from the NVAPI SDK headers (`nvapi_interface.h`,
`nvapi.h`, `nvapi_lite_common.h`) published at <https://github.com/NVIDIA/nvapi> under the MIT
License:

```
SPDX-FileCopyrightText: Copyright (c) 2019-2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
SPDX-License-Identifier: MIT

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

NVIDIA, GeForce, Surround and NVAPI are trademarks of NVIDIA Corporation. DisplayProfiles is not
affiliated with or endorsed by NVIDIA.

## Inno Setup

The Windows installer is built with Inno Setup (<https://jrsoftware.org/isinfo.php>), restored at build
time from the `Tools.InnoSetup` NuGet package. Inno Setup is not part of this repository. Installers
created with it are distributed under the Inno Setup license terms
(<https://jrsoftware.org/files/is/license.txt>).

## Microsoft .NET

The application runs on the .NET 8 Desktop Runtime and the ASP.NET Core 8 Runtime (MIT License,
<https://github.com/dotnet/runtime>, <https://github.com/dotnet/aspnetcore>). The runtimes are not
bundled; the installer downloads them from Microsoft if they are missing.
