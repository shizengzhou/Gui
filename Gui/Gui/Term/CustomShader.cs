using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace Gui.Term;

/// <summary>
/// Compiles Ghostty/Shadertoy-style GLSL into the pixel shader blob Win2D's
/// <c>PixelShaderEffect</c> consumes: GLSL → SPIR-V (shaderc) → HLSL (SPIRV-Cross) →
/// DXBC (D3DCompiler). Compiled blobs are cached by path and file timestamp.
/// </summary>
internal static unsafe class CustomShader
{
    private const int MaxSourceBytes = 4 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A compiled pixel shader and whether it samples the terminal texture.</summary>
    public sealed record Compiled(byte[] Bytecode, bool UsesChannel0);

    private sealed record CacheEntry(long Length, DateTime Written, Compiled? Shader);

    /// <summary>
    /// The Shadertoy preamble from Ghostty. A user shader only defines <c>mainImage</c>;
    /// every uniform and the fragment entry point come from here.
    /// </summary>
    private const string Prefix = """
#version 430 core

layout(binding = 1, std140) uniform Globals {
    uniform vec3  iResolution;
    uniform float iTime;
    uniform float iTimeDelta;
    uniform float iFrameRate;
    uniform int   iFrame;
    uniform float iChannelTime[4];
    uniform vec3  iChannelResolution[4];
    uniform vec4  iMouse;
    uniform vec4  iDate;
    uniform float iSampleRate;
    uniform vec4  iCurrentCursor;
    uniform vec4  iPreviousCursor;
    uniform vec4  iCurrentCursorColor;
    uniform vec4  iPreviousCursorColor;
    uniform int   iCurrentCursorStyle;
    uniform int   iPreviousCursorStyle;
    uniform int   iCursorVisible;
    uniform float iTimeCursorChange;
    uniform float iTimeFocus;
    uniform int iFocus;
    uniform vec3  iPalette[256];
    uniform vec3  iBackgroundColor;
    uniform vec3  iForegroundColor;
    uniform vec3  iCursorColor;
    uniform vec3  iCursorText;
    uniform vec3  iSelectionForegroundColor;
    uniform vec3  iSelectionBackgroundColor;
};

#define CURSORSTYLE_BLOCK        0
#define CURSORSTYLE_BLOCK_HOLLOW 1
#define CURSORSTYLE_BAR          2
#define CURSORSTYLE_UNDERLINE    3
#define CURSORSTYLE_LOCK         4

layout(binding = 0) uniform sampler2D iChannel0;

layout(location = 0) in vec4 gl_FragCoord;
layout(location = 0) out vec4 _fragColor;

#define texture2D texture

void mainImage( out vec4 fragColor, in vec2 fragCoord );
void main() { mainImage (_fragColor, gl_FragCoord.xy); }
""";

    /// <summary>
    /// Returns the compiled pixel shader for <paramref name="path"/>, or null when the file
    /// is missing, too large, or fails to compile. Failures are cached for the same file
    /// version so a broken shader is not recompiled on every frame.
    /// </summary>
    public static Compiled? Compile(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception error)
        {
            Diag.Warn("shader", $"{path}: {error.Message}");
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out CacheEntry? cached) &&
                cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
            {
                return cached.Shader;
            }
        }

        long length = info.Length;
        DateTime written = info.LastWriteTimeUtc;
        Compiled? shader;
        try
        {
            shader = CompileCore(path, out string? error);
            if (error is not null) Diag.Warn("shader", $"{path}: {error}");
        }
        catch (Exception error)
        {
            Diag.LogException("shader", error);
            shader = null;
        }

        lock (Gate) Cache[path] = new CacheEntry(length, written, shader);
        return shader;
    }

    private static Compiled? CompileCore(string path, out string? error)
    {
        error = null;
        string source;
        try
        {
            FileInfo info = new(path);
            if (info.Length > MaxSourceBytes)
            {
                error = $"shader is larger than {MaxSourceBytes} bytes";
                return null;
            }
            source = File.ReadAllText(path);
        }
        catch (Exception failure)
        {
            error = failure.Message;
            return null;
        }

        byte[] glsl = Encoding.UTF8.GetBytes(Prefix + "\n\n" + source);
        byte[] fileName = Encoding.UTF8.GetBytes(Path.GetFileName(path));
        byte[] entryPoint = Encoding.UTF8.GetBytes("main");

        var shaderc = Silk.NET.Shaderc.Shaderc.GetApi();
        Silk.NET.Shaderc.Compiler* compiler = shaderc.CompilerInitialize();
        Silk.NET.Shaderc.CompileOptions* options = shaderc.CompileOptionsInitialize();
        Silk.NET.Shaderc.CompilationResult* result = null;
        try
        {
            if (compiler is null || options is null)
            {
                error = "shaderc could not start";
                return null;
            }

            shaderc.CompileOptionsSetSourceLanguage(options, Silk.NET.Shaderc.SourceLanguage.Glsl);
            fixed (byte* sourcePointer = glsl)
            fixed (byte* namePointer = fileName)
            fixed (byte* entryPointer = entryPoint)
            {
                result = shaderc.CompileIntoSpv(compiler, sourcePointer, (nuint)glsl.Length,
                    Silk.NET.Shaderc.ShaderKind.FragmentShader, namePointer, entryPointer, options);
            }
            if (result is null)
            {
                error = "shaderc returned no result";
                return null;
            }
            if (shaderc.ResultGetCompilationStatus(result) != Silk.NET.Shaderc.CompilationStatus.Success)
            {
                error = shaderc.ResultGetErrorMessageS(result);
                return null;
            }

            nuint spirvLength = shaderc.ResultGetLength(result);
            byte* spirv = shaderc.ResultGetBytes(result);
            if (spirv is null || spirvLength == 0)
            {
                error = "shaderc produced no SPIR-V";
                return null;
            }

            string? hlsl = ToHlsl(spirv, spirvLength);
            if (hlsl is null)
            {
                error = "SPIR-V could not be converted to HLSL";
                return null;
            }

            // D2D accepts one constant buffer at b0 and looks shader properties up by name.
            // SPIRV-Cross uses the GLSL block binding and a private name prefix, so undo both.
            hlsl = Regex.Replace(hlsl, @"_\d+_(i[A-Za-z0-9_]+)", "$1");
            hlsl = Regex.Replace(hlsl,
                @"(cbuffer\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*register\(b)\d+(\))",
                "${1}0${2}");

            byte[]? bytecode = CompileDxbc(hlsl, path, out error);
            if (bytecode is null) return null;

            // The declaration can survive even when the texture is never read, so look for an
            // actual access. D2D rejects Source1 when the shader reflects zero inputs.
            bool usesChannel0 = Regex.IsMatch(hlsl, @"iChannel0\s*\.\s*(Sample|GetDimensions)");
            return new Compiled(bytecode, usesChannel0);
        }
        finally
        {
            if (result is not null) shaderc.ResultRelease(result);
            if (options is not null) shaderc.CompileOptionsRelease(options);
            if (compiler is not null) shaderc.CompilerRelease(compiler);
        }
    }

    /// <summary>Converts SPIR-V to HLSL through SPIRV-Cross and copies the text out.</summary>
    private static string? ToHlsl(byte* spirv, nuint spirvLength)
    {
        var cross = Silk.NET.SPIRV.Cross.Cross.GetApi();
        Silk.NET.SPIRV.Cross.Context* context = null;
        cross.ContextCreate(&context);
        if (context is null) return null;

        try
        {
            Silk.NET.SPIRV.Cross.ParsedIr* ir = null;
            cross.ContextParseSpirv(context, (uint*)spirv, spirvLength / 4, &ir);
            if (ir is null) return null;

            Silk.NET.SPIRV.Cross.Compiler* compiler = null;
            cross.ContextCreateCompiler(context, Silk.NET.SPIRV.Cross.Backend.Hlsl, ir,
                Silk.NET.SPIRV.Cross.CaptureMode.TakeOwnership, &compiler);
            if (compiler is null) return null;

            Silk.NET.SPIRV.Cross.CompilerOptions* options = null;
            cross.CompilerCreateCompilerOptions(compiler, &options);
            if (options is not null)
            {
                cross.CompilerOptionsSetUint(options,
                    Silk.NET.SPIRV.Cross.CompilerOption.HlslShaderModel, 40);
                cross.CompilerInstallCompilerOptions(compiler, options);
            }

            byte* text = null;
            cross.CompilerCompile(compiler, &text);
            return text is null ? null : Marshal.PtrToStringUTF8((IntPtr)text);
        }
        finally
        {
            cross.ContextDestroy(context);
        }
    }

    /// <summary>Compiles the generated HLSL with the system Direct3D shader compiler.</summary>
    private static byte[]? CompileDxbc(string hlsl, string path, out string? error)
    {
        error = null;
        byte[] source = Encoding.UTF8.GetBytes(hlsl);
        var result = Compiler.Compile(source, null!, null!,
            "main", path, "ps_4_0", ShaderFlags.OptimizationLevel1,
            out Blob code, out Blob errors);
        if (result.Failure)
        {
            error = errors is null
                ? result.Description
                : Encoding.UTF8.GetString(errors.AsSpan()).Trim();
            return null;
        }

        if (code is null)
        {
            error = "D3DCompile returned no shader";
            return null;
        }

        byte[] bytecode = code.AsSpan().ToArray();
        code.Dispose();
        errors?.Dispose();
        return bytecode;
    }
}
