#!/bin/bash
# 运行 SharpImageConverter
if [ -f src/bin/Debug/net10.0/SharpImageConverter.dll ]; then
  dotnet src/bin/Debug/net10.0/SharpImageConverter.dll "$@"
else
  dotnet src/bin/Debug/net9.0/SharpImageConverter.dll "$@"
fi
