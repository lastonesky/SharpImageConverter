#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="${OUT_DIR:-${out_dir:-}}"
if [[ -z "$OUT_DIR" ]]; then
  OUT_DIR="$(mktemp -d /tmp/sharpimageconverter-out.XXXXXX)"
else
  mkdir -p "$OUT_DIR"
  OUT_DIR="$(cd "$OUT_DIR" && pwd)"
fi

dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$SCRIPT_DIR/5_star_base.jpg" "$OUT_DIR/out1.gif"
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$SCRIPT_DIR/Amish-Noka-Dresser.jpg" "$OUT_DIR/out2.bmp"
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$SCRIPT_DIR/progressive.jpg" "$OUT_DIR/out3.png"
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$SCRIPT_DIR/video-001.cmyk.jpeg" "$OUT_DIR/out4.webp"
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$SCRIPT_DIR/5_star_base.jpg" "$OUT_DIR/out5.bmp" --gray
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$OUT_DIR/out5.bmp" "$OUT_DIR/out6.jpg"
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$SCRIPT_DIR/Amish-Noka-Dresser.jpg" "$OUT_DIR/out7.png" resizefit:200x200
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$SCRIPT_DIR/video-001.cmyk.jpeg" "$OUT_DIR/out8.webp" resizefit:1000x1000
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$OUT_DIR/out8.webp" "$OUT_DIR/out9.png"
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$OUT_DIR/out9.png" "$OUT_DIR/out10.bmp"
dotnet run --project "$ROOT_DIR/Cli/SharpImageConverter.Cli.csproj" -- "$OUT_DIR/out1.gif" "$OUT_DIR/out11.jpg"

printf "\n输出目录: %s\n" "$OUT_DIR"
printf "按回车键清理测试生成的 out*.jpg/png/webp/gif/bmp ...\n"
read -r
rm -f "$OUT_DIR"/out*.jpg "$OUT_DIR"/out*.png "$OUT_DIR"/out*.webp "$OUT_DIR"/out*.gif "$OUT_DIR"/out*.bmp
