using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retro96.Engine.Js;

namespace Retro96.Plugins;

internal static class PluginJsValueCodec
{
    // Mirrors the engine-side cap in FromEngineObject so both directions of
    // the script bridge accept the same shapes.
    private const int MaxWireElements = 4096;

    public static PluginSandboxProtocol.JsValueWire ToWire(JsValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            JsValueKind.Null => new("null"),
            JsValueKind.String => new("string", StringValue: value.StringValue ?? string.Empty),
            JsValueKind.Number => new("number", NumberValue: value.NumberValue),
            JsValueKind.Boolean => new("boolean", BooleanValue: value.BooleanValue),
            JsValueKind.Array => new("array", ArrayValue: value.ArrayValue!.Select(ToWire).ToArray()),
            JsValueKind.Object => new("object", ObjectValue: value.ObjectValue!.ToDictionary(p => p.Key, p => ToWire(p.Value), StringComparer.Ordinal)),
            _ => throw new InvalidOperationException("Unsupported plugin JavaScript value kind.")
        };
    }

    public static JsValue FromWire(PluginSandboxProtocol.JsValueWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return wire.Kind.ToLowerInvariant() switch
        {
            "null" => JsValue.Null,
            "string" => JsValue.From(wire.StringValue ?? string.Empty),
            "number" => JsValue.From(wire.NumberValue ?? throw new InvalidDataException("JavaScript number is missing.")),
            "boolean" => JsValue.From(wire.BooleanValue ?? throw new InvalidDataException("JavaScript boolean is missing.")),
            "array" => FromWireArray(wire.ArrayValue),
            "object" => FromWireObject(wire.ObjectValue),
            _ => throw new InvalidDataException($"Unsupported JavaScript value kind '{wire.Kind}'.")
        };
    }

    private static JsValue FromWireArray(PluginSandboxProtocol.JsValueWire[]? values)
    {
        var items = values ?? Array.Empty<PluginSandboxProtocol.JsValueWire>();
        if (items.Length > MaxWireElements)
            throw new InvalidDataException("Embedded script array is too large.");
        return JsValue.FromArray(items.Select(FromWire).ToArray());
    }

    private static JsValue FromWireObject(Dictionary<string, PluginSandboxProtocol.JsValueWire>? values)
    {
        var map = values ?? new Dictionary<string, PluginSandboxProtocol.JsValueWire>(StringComparer.Ordinal);
        if (map.Count > MaxWireElements)
            throw new InvalidDataException("Embedded script object is too large.");
        return JsValue.FromObject(map.ToDictionary(p => p.Key, p => FromWire(p.Value), StringComparer.Ordinal));
    }

    public static JsValue FromEngine(Retro96.Engine.Js.JsValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Type switch
        {
            JsType.Null or JsType.Undefined => JsValue.Null,
            JsType.String => JsValue.From(value.GetString()),
            JsType.Number => JsValue.From(value.GetNumber()),
            JsType.Boolean => JsValue.From(value.GetBool()),
            JsType.Object => FromEngineObject(value.GetObject()),
            _ => throw new InvalidDataException("Only null, string, number, bool, and flat object/array values may cross the embed script boundary.")
        };
    }

    public static Retro96.Engine.Js.JsValue ToEngine(JsValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            JsValueKind.Null => Retro96.Engine.Js.JsValue.Null,
            JsValueKind.String => Retro96.Engine.Js.JsValue.From(value.StringValue ?? string.Empty),
            JsValueKind.Number => Retro96.Engine.Js.JsValue.From(value.NumberValue),
            JsValueKind.Boolean => Retro96.Engine.Js.JsValue.From(value.BooleanValue),
            JsValueKind.Array => ToEngineArray(value),
            JsValueKind.Object => ToEngineObject(value),
            _ => throw new InvalidOperationException("Unsupported plugin JavaScript value kind.")
        };
    }

    private static JsValue FromEngineObject(Retro96.Engine.Js.JsObject obj)
    {
        if (obj.Class.Equals("Array", StringComparison.OrdinalIgnoreCase))
        {
            int length = 0;
            if (obj.Properties.TryGetValue("length", out var lengthValue) && lengthValue.Type == JsType.Number)
            {
                double raw = lengthValue.GetNumber();
                if (raw < 0 || raw > MaxWireElements || raw != Math.Floor(raw))
                    throw new InvalidDataException("Embedded script array is too large or malformed.");
                length = (int)raw;
            }
            var values = new JsValue[length];
            for (int i = 0; i < length; i++)
            {
                if (!obj.Properties.TryGetValue(i.ToString(), out var item)) item = Retro96.Engine.Js.JsValue.Null;
                values[i] = FromEnginePrimitive(item);
            }
            return JsValue.FromArray(values);
        }

        if (obj.Properties.Count > MaxWireElements) throw new InvalidDataException("Embedded script object is too large.");
        var result = new Dictionary<string, JsValue>(StringComparer.Ordinal);
        foreach (var pair in obj.Properties)
        {
            if (pair.Key == "length") continue;
            result[pair.Key] = FromEnginePrimitive(pair.Value);
        }
        return JsValue.FromObject(result);
    }

    private static JsValue FromEnginePrimitive(Retro96.Engine.Js.JsValue value)
    {
        return value.Type switch
        {
            JsType.Null or JsType.Undefined => JsValue.Null,
            JsType.String => JsValue.From(value.GetString()),
            JsType.Number => JsValue.From(value.GetNumber()),
            JsType.Boolean => JsValue.From(value.GetBool()),
            _ => throw new InvalidDataException("Only flat JavaScript values may cross the embed script boundary.")
        };
    }

    private static Retro96.Engine.Js.JsValue ToEngineArray(JsValue value)
    {
        var obj = new JsObject { Class = "Array" };
        var items = value.ArrayValue ?? Array.Empty<JsValue>();
        for (int i = 0; i < items.Count; i++) obj.Set(i.ToString(), ToEngine(items[i]));
        obj.Set("length", Retro96.Engine.Js.JsValue.From(items.Count));
        return Retro96.Engine.Js.JsValue.FromObject(obj);
    }

    private static Retro96.Engine.Js.JsValue ToEngineObject(JsValue value)
    {
        var obj = new JsObject();
        foreach (var pair in value.ObjectValue ?? new Dictionary<string, JsValue>(StringComparer.Ordinal))
            obj.Set(pair.Key, ToEngine(pair.Value));
        return Retro96.Engine.Js.JsValue.FromObject(obj);
    }
}