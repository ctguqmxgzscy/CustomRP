#!/usr/bin/env bash
# Custom RP 项目验证脚本
# 无需 Unity，检查项目结构、shader kernel 一致性、cbuffer 对齐
set -eo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
ASSETS="$PROJECT_ROOT/Assets"
FEATURES="$ASSETS/Features/ScreenSpaceReflection"
PASS=0
FAIL=0
WARN=0

red()   { echo -e "\e[31m$*\e[0m"; }
green() { echo -e "\e[32m$*\e[0m"; }
yellow(){ echo -e "\e[33m$*\e[0m"; }

check() {
    local label="$1" condition="$2"
    if eval "$condition"; then
        green "  [PASS] $label"
        PASS=$((PASS+1))
    else
        red "  [FAIL] $label"
        FAIL=$((FAIL+1))
    fi
}

warn() {
    yellow "  [WARN] $1"
    WARN=$((WARN+1))
}

echo "=== Custom RP Project Validation ==="
echo "Project: $PROJECT_ROOT"
echo ""

# ── 1. File existence ──
echo "── Source files ──"
check "ScreenSpaceReflection.cs"      'test -f "$FEATURES/Runtime/ScreenSpaceReflection.cs"'
check "ShaderVariablesScreenSpaceReflection.cs" 'test -f "$FEATURES/Runtime/ShaderVariablesScreenSpaceReflection.cs"'
check "ShaderVariablesScreenSpaceReflection.cs.hlsl" 'test -f "$FEATURES/Runtime/ShaderVariablesScreenSpaceReflection.cs.hlsl"'
check "DepthPyramidGenerator.cs"      'test -f "$FEATURES/Runtime/DepthPyramidGenerator.cs"'
check "ColorPyramidGenerator.cs"      'test -f "$FEATURES/Runtime/ColorPyramidGenerator.cs"'
check "DepthPyramid.compute"          'test -f "$FEATURES/Runtime/Shaders/DepthPyramid.compute"'
check "ScreenSpaceReflections.compute" 'test -f "$FEATURES/Runtime/Shaders/ScreenSpaceReflections.compute"'
check "SSRComposite.shader"            'test -f "$FEATURES/Runtime/Shaders/SSRComposite.shader"'
check "ScreenSpaceReflectionEditor.cs" 'test -f "$FEATURES/Editor/ScreenSpaceReflectionEditor.cs"'
check "ScreenSpaceReflectionFeature.cs" 'test -f "$ASSETS/Scripts/Features/ScreenSpaceReflectionFeature.cs"'

echo ""
echo "── Config files ──"
check "ProjectSettings.asset"  'test -f "$PROJECT_ROOT/ProjectSettings/ProjectSettings.asset"'
check "GraphicsSettings.asset" 'test -f "$PROJECT_ROOT/ProjectSettings/GraphicsSettings.asset"'
check "manifest.json"          'test -f "$PROJECT_ROOT/Packages/manifest.json"'

# ── 2. Kernel consistency ──
echo ""
echo "── Shader kernel consistency ──"

# Extract C# FindKernel calls
CS_KERNELS=$(grep -roh 'FindKernel("[^"]*")' "$ASSETS" --include="*.cs" 2>/dev/null | sed 's/FindKernel("\(.*\)")/\1/' | sort -u || true)
# Extract #pragma kernel declarations
SHADER_KERNELS=$(grep -roh '#pragma kernel [A-Za-z0-9_]*' "$ASSETS" --include="*.compute" 2>/dev/null | sed 's/#pragma kernel //' | sort -u || true)

# Kernels in C# but not in shader → will fail at runtime
for k in $CS_KERNELS; do
    if echo "$SHADER_KERNELS" | grep -qx "$k"; then
        green "  [PASS] $k (C# → shader)"
        ((PASS++))
    else
        red "  [FAIL] $k referenced in C# but NOT in any .compute shader"
        ((FAIL++))
    fi
done

# Kernels in shader but not in C# → unused (P3: Gaussian kernels)
for k in $SHADER_KERNELS; do
    if ! echo "$CS_KERNELS" | grep -qx "$k"; then
        warn "$k declared in .compute but NOT referenced in C# (unused)"
        WARN=$((WARN+1))
    fi
done

# ── 3. cbuffer alignment ──
echo ""
echo "── cbuffer alignment (C# ↔ .cs.hlsl) ──"

CSPATH="$FEATURES/Runtime/ShaderVariablesScreenSpaceReflection.cs"
HLSLPATH="$FEATURES/Runtime/ShaderVariablesScreenSpaceReflection.cs.hlsl"

if test -f "$CSPATH" && test -f "$HLSLPATH"; then
    # Extract float/int fields from C# (public float/int name;)
    CS_FIELDS=$(grep -oE '(float|int)\s+\w+' "$CSPATH" | grep -v '//' | awk '{print $2}' | sort || true)
    # Extract float/int fields from HLSL cbuffer
    HLSL_FIELDS=$(grep -oE '(float|int)\s+\w+' "$HLSLPATH" | awk '{print $2}' | sort || true)

    for f in $CS_FIELDS; do
        if echo "$HLSL_FIELDS" | grep -qx "$f"; then
            :  # pass — too verbose to list all
        else
            red "  [FAIL] '$f' in C# but missing in .cs.hlsl"
            ((FAIL++))
        fi
    done

    for f in $HLSL_FIELDS; do
        if ! echo "$CS_FIELDS" | grep -qx "$f"; then
            red "  [FAIL] '$f' in .cs.hlsl but missing in C#"
            ((FAIL++))
        fi
    done

    # If no mismatches printed, count as pass
    CS_COUNT=$(echo "$CS_FIELDS" | wc -l || true)
    HLSL_COUNT=$(echo "$HLSL_FIELDS" | wc -l || true)
    if [ "$CS_COUNT" -eq "$HLSL_COUNT" ]; then
        green "  [PASS] C# and .cs.hlsl have $CS_COUNT matching fields"
        ((PASS++))
    fi
else
    red "  [FAIL] Missing cbuffer source file(s)"
    ((FAIL++))
fi

# ── 4. Known issues scan ──
echo ""
echo "── Known issues ──"

# P3: Check Gaussian pyramid path is actually activated (not just GenerateMips fallback)
if grep -q 'GenerateGaussianMips' "$FEATURES/Runtime/ColorPyramidGenerator.cs" 2>/dev/null; then
    green "  [PASS] P3: Gaussian Color Pyramid path activated (KColorDownsample→H→V)"
    ((PASS++))
elif grep -q 'cmd.GenerateMips' "$FEATURES/Runtime/ColorPyramidGenerator.cs" 2>/dev/null; then
    warn "P3: Color Pyramid uses GenerateMips (box filter), Gaussian kernels not activated"
fi

if grep -qE 'using UnityEditor;' "$ASSETS/Scripts/Features/ScreenSpaceReflectionFeature.cs" 2>/dev/null; then
    if ! head -5 "$ASSETS/Scripts/Features/ScreenSpaceReflectionFeature.cs" 2>/dev/null | grep -q '#if UNITY_EDITOR'; then
        warn "using UnityEditor; not guarded with #if UNITY_EDITOR"
    fi
fi

# P7: Trace towards eye — now a runtime switch instead of compile-time #define
if grep -q '_SsrTraceTowardsEye' "$FEATURES/Runtime/Shaders/ScreenSpaceReflections.compute" 2>/dev/null; then
    green "  [PASS] P7: SSR_TRACE_TOWARDS_EYE as runtime switch (_SsrTraceTowardsEye)"
    ((PASS++))
elif grep -q '#define SSR_TRACE_TOWARDS_EYE' "$FEATURES/Runtime/Shaders/ScreenSpaceReflections.compute" 2>/dev/null; then
    green "  [PASS] P7: SSR_TRACE_TOWARDS_EYE enabled (compile-time)"
    ((PASS++))
else
    warn "P7: SSR_TRACE_TOWARDS_EYE disabled (HDRP has it enabled)"
fi

# P4: Check 5-sample cross block sample is active (aligned with HDRP SSR_REPROJECT PBR)
if grep -q 'BLOCK_SAMPLE_RADIUS' "$FEATURES/Runtime/Shaders/ScreenSpaceReflections.compute" 2>/dev/null; then
    green "  [PASS] P4: 5-sample cross block sample active (对齐 HDRP SSR_REPROJECT PBR)"
    ((PASS++))
else
    warn "P4: PBR 模式缺少 block sample (HDRP SSR_REPROJECT 有 BLOCK_SAMPLE_RADIUS 1)"
fi

# ── 5. Summary ──
echo ""
echo "═══════════════════════════════════"
echo "  Pass: $PASS  Fail: $FAIL  Warn: $WARN"
echo "═══════════════════════════════════"

if [ "$FAIL" -gt 0 ]; then
    red "Validation FAILED — $FAIL issue(s) need attention"
    exit 1
else
    green "Validation PASSED"
    exit 0
fi
