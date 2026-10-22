using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Jellyfin.Plugin.RaceToFinish.Services;

/// <summary>
/// Calls into other plugins (File Transformation, Home Screen Sections) without referencing them.
/// Jellyfin loads each plugin in its own load context, so a direct reference would not bind.
/// Their entry points take a Newtonsoft <c>JObject</c>; we build it with *their* copy of Newtonsoft
/// (found from the method's own parameter type), so this plugin needs no Newtonsoft dependency.
/// </summary>
public static class PluginBridge
{
    /// <summary>Finds a loaded assembly whose name contains <paramref name="nameFragment"/>.</summary>
    /// <param name="nameFragment">Part of the assembly name, e.g. ".FileTransformation".</param>
    /// <returns>The assembly, or null when that plugin is not installed or not loaded.</returns>
    public static Assembly? FindAssembly(string nameFragment)
    {
        return AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(assembly => assembly.FullName?.Contains(nameFragment, StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// Invokes <c>{typeName}.{methodName}(JObject)</c> in the given plugin assembly with <paramref name="payload"/> as JSON.
    /// </summary>
    /// <param name="assembly">The other plugin's assembly.</param>
    /// <param name="typeName">Full name of its static interface class.</param>
    /// <param name="methodName">Static method taking one JObject.</param>
    /// <param name="payload">Payload, serialized with camelCase-free property names exactly as given.</param>
    /// <returns>Null on success, otherwise a short reason for the log.</returns>
    public static string? InvokeWithJson(Assembly assembly, string typeName, string methodName, IDictionary<string, string> payload)
    {
        Type? interfaceType = assembly.GetType(typeName);
        if (interfaceType is null)
        {
            return $"type {typeName} not found";
        }

        MethodInfo? method = interfaceType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        ParameterInfo[]? parameters = method?.GetParameters();
        if (method is null || parameters is null || parameters.Length != 1)
        {
            return $"method {methodName} not found";
        }

        Type jsonObjectType = parameters[0].ParameterType;
        MethodInfo? parse = jsonObjectType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (parse is null)
        {
            return $"cannot build {jsonObjectType.FullName}";
        }

        object? jsonObject = parse.Invoke(null, [JsonSerializer.Serialize(payload)]);
        method.Invoke(null, [jsonObject]);
        return null;
    }
}
