#nullable enable
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Baklava.Helpers;

/// <summary>
/// Reflection boundary for the optional Gelato plugin.
/// </summary>
internal static class GelatoCompatibility
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    public static Assembly? GetAssembly() => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => string.Equals(a.GetName().Name, "Gelato", StringComparison.Ordinal));

    public static object? GetPluginInstance(Assembly assembly) => assembly
        .GetType("Gelato.GelatoPlugin")
        ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
        ?.GetValue(null);

    public static object? GetManager(IServiceProvider services, Assembly assembly)
    {
        var managerType = assembly.GetType("Gelato.GelatoManager");
        if (managerType is null)
        {
            return null;
        }

        // Gelato registers its manager as a singleton. Reusing that instance also preserves its
        // caches and decorated repositories; manually reconstructing it bypasses that setup.
        var plugin = GetPluginInstance(assembly);
        return services.GetService(managerType)
            ?? plugin
                ?.GetType()
                .GetField("_manager", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(plugin);
    }

    public static object? GetConfig(Assembly assembly, Guid userId)
    {
        var plugin = GetPluginInstance(assembly);
        return plugin?.GetType().GetMethod("GetConfig", PublicInstance)?.Invoke(plugin, [userId]);
    }

    public static object? GetStremioProvider(object config)
    {
        var type = config.GetType();
        return type.GetField("Stremio", PublicInstance)?.GetValue(config)
            ?? type.GetField("stremio", PublicInstance)?.GetValue(config)
            ?? type.GetProperty("Stremio", PublicInstance)?.GetValue(config)
            ?? type.GetProperty("stremio", PublicInstance)?.GetValue(config);
    }

    public static Type? GetMediaType(Assembly assembly) => assembly.GetType("Gelato.StremioMediaType");

    public static object ParseMediaType(Type mediaType, bool series) =>
        Enum.Parse(mediaType, series ? "Series" : "Movie", ignoreCase: true);

    public static async Task<object?> GetMetaAsync(
        object stremioProvider,
        string id,
        object mediaType)
    {
        var method = stremioProvider.GetType()
            .GetMethods(PublicInstance)
            .Where(m => string.Equals(m.Name, "GetMetaAsync", StringComparison.Ordinal))
            .Select(m => new { Method = m, Parameters = m.GetParameters() })
            .FirstOrDefault(x =>
                x.Parameters.Length is 2 or 3
                && x.Parameters[0].ParameterType == typeof(string)
                && x.Parameters[1].ParameterType.IsInstanceOfType(mediaType));

        if (method is null)
        {
            return null;
        }

        object?[] args = method.Parameters.Length == 3
            ? [id, mediaType, null]
            : [id, mediaType];
        return await InvokeTaskResultAsync(method.Method, stremioProvider, args).ConfigureAwait(false);
    }

    public static async Task<IList?> GetStreamsAsync(
        Assembly assembly,
        object stremioProvider,
        string id,
        object mediaType)
    {
        var providerType = stremioProvider.GetType();
        var uriType = assembly.GetType("Gelato.StremioUri");
        if (uriType is not null)
        {
            var uri = Activator.CreateInstance(uriType, [mediaType, id, null]);
            var uriMethod = providerType.GetMethod("GetStreamsAsync", PublicInstance, null, [uriType], null);
            if (uri is not null && uriMethod is not null)
            {
                return await InvokeTaskResultAsync(uriMethod, stremioProvider, [uri])
                    .ConfigureAwait(false) as IList;
            }
        }

        // Compatibility with older Gelato builds that exposed the id/type overload publicly.
        var legacy = providerType.GetMethod(
            "GetStreamsAsync",
            PublicInstance,
            null,
            [typeof(string), mediaType.GetType()],
            null);
        return legacy is null
            ? null
            : await InvokeTaskResultAsync(legacy, stremioProvider, [id, mediaType])
                .ConfigureAwait(false) as IList;
    }

    public static async Task<IEnumerable?> GetSubtitlesForItemAsync(
        Assembly assembly,
        object stremioProvider,
        BaseItem item)
    {
        var uriType = assembly.GetType("Gelato.StremioUri")
            ?? assembly.GetType("Gelato.Common.StremioUri");
        var fromItem = uriType?.GetMethod("FromBaseItem", BindingFlags.Public | BindingFlags.Static);
        var uri = fromItem?.Invoke(null, [item]);
        if (uri is null)
        {
            return null;
        }

        var id = uriType!.GetProperty("ExternalId", PublicInstance)?.GetValue(uri) as string;
        var mediaType = uriType.GetProperty("MediaType", PublicInstance)?.GetValue(uri);
        if (string.IsNullOrWhiteSpace(id) || mediaType is null)
        {
            return null;
        }

        var method = stremioProvider.GetType().GetMethod(
            "GetSubtitlesAsync",
            PublicInstance,
            null,
            [typeof(string), mediaType.GetType()],
            null);
        return method is null
            ? null
            : await InvokeTaskResultAsync(method, stremioProvider, [id, mediaType])
                .ConfigureAwait(false) as IEnumerable;
    }

    public static object? GetRootFolder(object manager, bool series, Guid userId)
    {
        var methodName = series ? "TryGetSeriesFolder" : "TryGetMovieFolder";
        return manager.GetType()
            .GetMethod(methodName, PublicInstance, null, [typeof(Guid)], null)
            ?.Invoke(manager, [userId]);
    }

    public static object CreateMeta(Assembly assembly, string id, object mediaType)
    {
        var metaType = assembly.GetType("Gelato.StremioMeta")
            ?? throw new InvalidOperationException("Gelato.StremioMeta was not found.");
        var meta = Activator.CreateInstance(metaType)
            ?? throw new InvalidOperationException("Gelato.StremioMeta could not be created.");
        metaType.GetProperty("Id", PublicInstance)?.SetValue(meta, id);
        metaType.GetProperty("ImdbId", PublicInstance)?.SetValue(meta, id);
        metaType.GetProperty("Type", PublicInstance)?.SetValue(meta, mediaType);
        return meta;
    }

    public static async Task<object?> InsertMetaAsync(
        object manager,
        object parentFolder,
        object meta,
        object? user,
        bool allowRemoteRefresh,
        bool refreshItem,
        bool queueRefreshItem,
        CancellationToken cancellationToken)
    {
        var method = manager.GetType()
            .GetMethods(PublicInstance)
            .Where(m => string.Equals(m.Name, "InsertMeta", StringComparison.Ordinal))
            .Select(m => new { Method = m, Parameters = m.GetParameters() })
            .FirstOrDefault(x =>
                x.Parameters.Length is >= 7 and <= 9
                && x.Parameters[0].ParameterType.IsInstanceOfType(parentFolder)
                && x.Parameters[1].ParameterType.IsInstanceOfType(meta)
                && x.Parameters[6].ParameterType == typeof(CancellationToken));
        if (method is null)
        {
            return null;
        }

        var actorType = method.Parameters[2].ParameterType;
        object? actor = user is not null && actorType.IsInstanceOfType(user)
            ? user
            : actorType == typeof(Guid)
                ? Guid.Empty
                : null;

        object?[] args = new object?[method.Parameters.Length];
        object?[] required = [parentFolder, meta, actor, allowRemoteRefresh, refreshItem, queueRefreshItem, cancellationToken];
        Array.Copy(required, args, required.Length);
        for (var i = required.Length; i < args.Length; i++)
            args[i] = method.Parameters[i].HasDefaultValue ? method.Parameters[i].DefaultValue : false;
        var result = await InvokeTaskResultAsync(method.Method, manager, args).ConfigureAwait(false);
        return result?.GetType().GetField("Item1", PublicInstance)?.GetValue(result) ?? result;
    }

    public static string? GetItemId(object? item) =>
        item?.GetType().GetProperty("Id", PublicInstance)?.GetValue(item)?.ToString();

    private static async Task<object?> InvokeTaskResultAsync(
        MethodInfo method,
        object target,
        object?[] args)
    {
        if (method.Invoke(target, args) is not Task task)
        {
            return null;
        }

        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result", PublicInstance)?.GetValue(task);
    }
}
