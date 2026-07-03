namespace SharpLensMcp;

// The two tool-response envelope shapes, promoted from anonymous objects to named
// records so internal callers read them by typed pattern match instead of
// string-keyed reflection. The property names are intentionally lowercase so both
// serializers emit the exact same top-level keys as the former anonymous objects:
// the wire path (System.Text.Json, camelCase policy) and the in-process test path
// (Newtonsoft JObject.FromObject, no policy) both produce success/data/meta/error
// with no attributes and no serializer coupling. The meta/error sub-objects keep
// their PascalCase-in-process / camelCase-on-wire behavior, unchanged.
internal sealed record ToolSuccessResponse(bool success, object data, ResponseMetadata meta);

internal sealed record ToolErrorResponse(bool success, RoslynError error);
