#!/bin/bash
# 运行 PictureSharp
if [ -f src/bin/Debug/net10.0/PictureSharp.dll ]; then
  dotnet src/bin/Debug/net10.0/PictureSharp.dll "$@"
else
  dotnet src/bin/Debug/net9.0/PictureSharp.dll "$@"
fi
