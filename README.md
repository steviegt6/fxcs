# fxcs

> d3dcompiler hacking

---

**fxcs** is an alternative host for `d3dcompiler_47.dll` targeted for Terraria/tModLoader DirectX 9 Effects Framework shader compilation.

It aims to be compatible with `fxc.exe`, but minor details are rather unimportant. The only requirement is that `fx_2_0` programs must still compile correctly.

`d3dcompiler_47.dll` is packaged with our builds, but you can find them on your own system. I personally picked out the one here:

```
C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64
```

## Why?

For basically the same purposes as [KBO.FXC](https://github.com/ProjectSlowRush/KBO.FXC):

- proper handling of `#include <file>` and `#include "file"`;
- improved handling of UTF-8 BOM files (that is, removing the BOM during compilation to prevent this cryptic error: `error X3000: Illegal character in shader file`).

More to come later.

## Usage

Download the latest release binaries and use the program as you would `fxc.exe`.

It's a lot better than EasyXNB and fxcompiler.
