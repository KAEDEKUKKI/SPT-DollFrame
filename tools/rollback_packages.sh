#!/usr/bin/env bash
# 一鍵回退：從原始 MMD PMX 重打包 19 包【未重製】版至遊戲 plugins（0.3.1 前的原作比例包）
# 用法：bash tools/rollback_packages.sh [-o <輸出目錄>]
set -euo pipefail
SRC="/run/media/kukki/HDD/MMD/少女前線2追放"
OUT="${2:-C:\SPT/BepInEx/plugins/kukki.GF2/GF2_Characters}"
cd "$(dirname "$0")/.."
export DOTNET_ROLL_FORWARD=LatestMajor
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
ok=0; fail=0
declare -A F=(
  [克萊妲原皮]="GirlsFrontline KoledaDefault.pmx"
  [代理人原皮]="GirlsFrontline AgentSSR01.pmx"
  [可露凱原皮]="GirlsFrontline ClukayDefault.pmx"
  [埃芙原皮]="GirlsFrontline AlvaDefault.pmx"
  [妮基塔原皮]="GirlsFrontline NikketaDefault.pmx"
  [威瑪西娜原皮]="GirlsFrontline VoymastinaDefault.pmx"
  [安朵絲原皮]="GirlsFrontline AndorisDefault.pmx"
  [希麗雅原皮]="GirlsFrontline CeciliaSSR01.pmx"
  [托洛洛原皮]="GirlsFrontline TololoDefault.pmx"
  [春田原皮]="GirlsFrontline SpringfieldDefault.pmx"
  [洛貝拉原皮]="GirlsFrontline RobellaDefault.pmx"
  [琳德原皮]="GirlsFrontline LindDefault.pmx"
  [瑪綺朵原皮]="GirlsFrontline MacqiatoDefault.pmx"
  [索普原皮]="GirlsFrontline SoppoDefault.pmx"
  [絳雨原皮]="GirlsFrontline JiangYuDefault.pmx"
  [萊婭原皮]="GirlsFrontline LevaDefault.pmx"
  [阿斯緹亞原皮]="GirlsFrontline AsteriaDefault.pmx"
  [黛煙原皮]="GirlsFrontline DaiYanDefault Ver 2.1.pmx"
  [萊娜-秘夜獵影]="GirlsFrontline LennaCommanderSkin Renew.pmx"
)
for pkg in "${!F[@]}"; do
  src="$SRC/$pkg/${F[$pkg]}"
  if dotnet Packager/bin/Release/net8.0/GF2Packager.dll "$src" -o "$OUT" >/dev/null 2>&1; then
    ok=$((ok+1))
  else
    echo "FAIL: $pkg"; fail=$((fail+1))
  fi
done
echo "rollback done: ok=$ok fail=$fail -> $OUT ($(ls "$OUT"/*.gfc | wc -l) packages)"
