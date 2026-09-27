using System.Buffers.Binary;
using System.Text;

namespace Retro96.Engine.Java;

public sealed class BigEndianReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _pos;
    public BigEndianReader(ReadOnlyMemory<byte> data) => _data = data;
    public int Position => _pos;
    public bool End => _pos >= _data.Length;
    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos > _data.Length - n) throw new InvalidDataException("Unexpected end of class file.");
        var s = _data.Span.Slice(_pos, n); _pos += n; return s;
    }
    public byte U1() => Take(1)[0];
    public sbyte S1() => unchecked((sbyte)U1());
    public ushort U2() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public short S2() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public uint U4() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public int S4() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public long S8() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public byte[] Bytes(int n) => Take(n).ToArray();
    public void Skip(int n) => _ = Take(n);
}

public abstract record JConstant(byte Tag);
public sealed record JUtf8(string Value) : JConstant(1);
public sealed record JInteger(int Value) : JConstant(3);
public sealed record JFloat(float Value) : JConstant(4);
public sealed record JLong(long Value) : JConstant(5);
public sealed record JDouble(double Value) : JConstant(6);
public sealed record JClassRef(ushort NameIndex) : JConstant(7);
public sealed record JStringRef(ushort StringIndex) : JConstant(8);
public sealed record JFieldRef(ushort ClassIndex, ushort NameAndTypeIndex) : JConstant(9);
public sealed record JMethodRef(ushort ClassIndex, ushort NameAndTypeIndex) : JConstant(10);
public sealed record JInterfaceMethodRef(ushort ClassIndex, ushort NameAndTypeIndex) : JConstant(11);
public sealed record JNameAndType(ushort NameIndex, ushort DescriptorIndex) : JConstant(12);

public sealed record ExceptionHandler(ushort StartPc, ushort EndPc, ushort HandlerPc, ushort CatchType);

public sealed class JAttribute
{
    public string Name { get; init; } = "";
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    public JCodeAttribute? Code { get; init; }
    public ushort? ConstantValueIndex { get; init; }
    public ushort[]? Exceptions { get; init; }
    public ushort? SourceFileIndex { get; init; }
}

public sealed class JCodeAttribute
{
    public ushort MaxStack { get; init; }
    public ushort MaxLocals { get; init; }
    public byte[] Code { get; init; } = Array.Empty<byte>();
    public ExceptionHandler[] ExceptionTable { get; init; } = Array.Empty<ExceptionHandler>();
    public JAttribute[] Attributes { get; init; } = Array.Empty<JAttribute>();
}

public sealed class JField
{
    public ushort AccessFlags { get; init; }
    public string Name { get; init; } = "";
    public string Descriptor { get; init; } = "";
    public JAttribute[] Attributes { get; init; } = Array.Empty<JAttribute>();
}

public sealed class JMethod
{
    public ushort AccessFlags { get; init; }
    public string Name { get; init; } = "";
    public string Descriptor { get; init; } = "";
    public JAttribute[] Attributes { get; init; } = Array.Empty<JAttribute>();
    /// <summary>Class that declared this method; used so interpreted frames resolve
    /// constant-pool entries against the correct class file (FIX: was resolved
    /// against the receiver's class, reading the wrong constant pool).</summary>
    public JClass? DeclaringClass { get; set; }
    private JCodeAttribute? _code;
    private bool _codeResolved;
    public JCodeAttribute? Code
    {
        get
        {
            if (!_codeResolved) { _code = Attributes.FirstOrDefault(a => a.Code != null)?.Code; _codeResolved = true; }
            return _code;
        }
    }
    public bool IsStatic => (AccessFlags & 0x0008) != 0;
    public bool IsNative => (AccessFlags & 0x0100) != 0;
    public bool IsAbstract => (AccessFlags & 0x0400) != 0;
    public bool IsSynchronized => (AccessFlags & 0x0020) != 0;
}

public sealed class JClassFile
{
    public const uint Magic = 0xCAFEBABE;
    public ushort MinorVersion { get; init; }
    public ushort MajorVersion { get; init; }
    public JConstant?[] ConstantPool { get; init; } = Array.Empty<JConstant?>();
    public ushort AccessFlags { get; init; }
    public ushort ThisClassIndex { get; init; }
    public ushort SuperClassIndex { get; init; }
    public ushort[] Interfaces { get; init; } = Array.Empty<ushort>();
    public JField[] Fields { get; init; } = Array.Empty<JField>();
    public JMethod[] Methods { get; init; } = Array.Empty<JMethod>();
    public JAttribute[] Attributes { get; init; } = Array.Empty<JAttribute>();

    public string ThisClassName => ClassNameFromIndex(ThisClassIndex);
    public string? SuperClassName => SuperClassIndex == 0 ? null : ClassNameFromIndex(SuperClassIndex);

    public string Utf8(ushort index) => (ConstantPool[index] as JUtf8)?.Value
        ?? throw new InvalidDataException($"Constant #{index} is not Utf8.");

    public string ClassNameFromIndex(ushort index)
    {
        var c = ConstantPool[index] as JClassRef ?? throw new InvalidDataException("Bad class reference.");
        return Utf8(c.NameIndex).Replace('/', '.');
    }

    public (string Owner, string Name, string Descriptor) Ref(ushort index) => RefValue(index);

    public (string Owner, string Name, string Descriptor) RefValue(ushort index)
    {
        var c = ConstantPool[index] ?? throw new InvalidDataException("Bad constant pool reference.");
        ushort cls, nt;
        switch (c)
        {
            case JFieldRef f: cls = f.ClassIndex; nt = f.NameAndTypeIndex; break;
            case JMethodRef m: cls = m.ClassIndex; nt = m.NameAndTypeIndex; break;
            case JInterfaceMethodRef i: cls = i.ClassIndex; nt = i.NameAndTypeIndex; break;
            default: throw new InvalidDataException("Constant is not a member reference.");
        }
        var nameType = ConstantPool[nt] as JNameAndType ?? throw new InvalidDataException("Bad NameAndType.");
        return (ClassNameFromIndex(cls), Utf8(nameType.NameIndex), Utf8(nameType.DescriptorIndex));
    }

    public JConstant Constant(ushort index) => ConstantPool[index]
        ?? throw new InvalidDataException("Invalid constant pool hole.");

    public static JClassFile Parse(ReadOnlyMemory<byte> bytes)
    {
        var r = new BigEndianReader(bytes);
        if (r.U4() != Magic) throw new InvalidDataException("Not a Java class file.");
        ushort minor = r.U2(), major = r.U2();
        // Primary target is 45.0-45.3 (JDK 1.0/1.1); accept later majors that
        // use the same constant-pool tag set so mixed-era pages degrade gracefully.
        if (major is < 45 or > 51)
            throw new InvalidDataException($"Unsupported class version {major}.{minor}; expected 45.x (JDK 1.0/1.1).");

        ushort cpCount = r.U2();
        var cp = new JConstant?[cpCount];
        for (int i = 1; i < cpCount; i++)
        {
            byte tag = r.U1();
            cp[i] = tag switch
            {
                1 => new JUtf8(DecodeModifiedUtf8(r.Bytes(r.U2()))),
                3 => new JInteger(r.S4()),
                4 => new JFloat(BitConverter.Int32BitsToSingle(r.S4())),
                5 => new JLong(r.S8()),
                6 => new JDouble(BitConverter.Int64BitsToDouble(r.S8())),
                7 => new JClassRef(r.U2()),
                8 => new JStringRef(r.U2()),
                9 => new JFieldRef(r.U2(), r.U2()),
                10 => new JMethodRef(r.U2(), r.U2()),
                11 => new JInterfaceMethodRef(r.U2(), r.U2()),
                12 => new JNameAndType(r.U2(), r.U2()),
                _ => throw new InvalidDataException($"Unsupported constant-pool tag {tag} at #{i}.")
            };
            if (tag is 5 or 6)
            {
                // Long/Double occupy two constant-pool slots; index i+1 is a hole
                // and must never be dereferenced.
                if (++i >= cpCount) throw new InvalidDataException("Long/Double runs past constant pool.");
                cp[i] = null;
            }
        }

        ushort access = r.U2(), thisClass = r.U2(), superClass = r.U2();
        ushort interfacesCount = r.U2();
        ushort[] interfaces = ReadArray(r, interfacesCount, () => r.U2());
        ushort fieldsCount = r.U2();
        var fields = new JField[fieldsCount];
        for (int i = 0; i < fields.Length; i++)
            fields[i] = new JField { AccessFlags = r.U2(), Name = Utf8(cp, r.U2()), Descriptor = Utf8(cp, r.U2()), Attributes = ReadAttributes(r, cp) };

        ushort methodsCount = r.U2();
        var methods = new JMethod[methodsCount];
        for (int i = 0; i < methods.Length; i++)
            methods[i] = new JMethod { AccessFlags = r.U2(), Name = Utf8(cp, r.U2()), Descriptor = Utf8(cp, r.U2()), Attributes = ReadAttributes(r, cp) };

        return new JClassFile
        {
            MinorVersion = minor,
            MajorVersion = major,
            ConstantPool = cp,
            AccessFlags = access,
            ThisClassIndex = thisClass,
            SuperClassIndex = superClass,
            Interfaces = interfaces,
            Fields = fields,
            Methods = methods,
            Attributes = ReadAttributes(r, cp)
        };
    }

    private static string DecodeModifiedUtf8(byte[] bytes)
    {
        var chars = new List<char>(bytes.Length);
        for (int i = 0; i < bytes.Length;)
        {
            int b = bytes[i++];
            if (b == 0xC0 && i < bytes.Length && bytes[i] == 0x80) { i++; chars.Add('\0'); continue; } // encoded NUL
            if (b < 0x80) { chars.Add((char)b); continue; }
            if ((b & 0xE0) == 0xC0)
            {
                if (i >= bytes.Length) throw new InvalidDataException("Truncated modified UTF-8 sequence.");
                int b2 = bytes[i++]; chars.Add((char)(((b & 0x1F) << 6) | (b2 & 0x3F))); continue;
            }
            if ((b & 0xF0) == 0xE0)
            {
                if (i + 1 >= bytes.Length) throw new InvalidDataException("Truncated modified UTF-8 sequence.");
                int b2 = bytes[i++], b3 = bytes[i++]; chars.Add((char)(((b & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F))); continue;
            }
            throw new InvalidDataException("Invalid modified UTF-8 byte in class file.");
        }
        return new string(chars.ToArray());
    }

    private static ushort[] ReadArray(BigEndianReader r, int count, Func<ushort> next)
    {
        var a = new ushort[count];
        for (int i = 0; i < count; i++) a[i] = next();
        return a;
    }

    private static string Utf8(JConstant?[] cp, ushort index) => (cp[index] as JUtf8)?.Value
        ?? throw new InvalidDataException("Expected Utf8 constant.");

    private static JAttribute[] ReadAttributes(BigEndianReader r, JConstant?[] cp)
    {
        ushort count = r.U2();
        var result = new JAttribute[count];
        for (int i = 0; i < count; i++)
        {
            string name = Utf8(cp, r.U2());
            uint len = r.U4();
            if (len > int.MaxValue) throw new InvalidDataException("Attribute too large.");
            var raw = r.Bytes((int)len);
            // FIX: fully construct init-only attributes in one object initializer
            // (assigning `a.Code = ...` after construction does not compile).
            result[i] = new JAttribute
            {
                Name = name,
                Raw = raw,
                Code = name == "Code" ? ParseCode(raw, cp) : null,
                ConstantValueIndex = name == "ConstantValue" && raw.Length >= 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(0, 2)) : null,
                Exceptions = name == "Exceptions" ? ParseExceptions(raw) : null,
                SourceFileIndex = name == "SourceFile" && raw.Length >= 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(0, 2)) : null
            };
        }
        return result;
    }

    private static JCodeAttribute ParseCode(byte[] raw, JConstant?[] cp)
    {
        var r = new BigEndianReader(raw);
        ushort maxStack = r.U2(), maxLocals = r.U2();
        int codeLen = checked((int)r.U4());
        byte[] code = r.Bytes(codeLen);
        ushort exCount = r.U2();
        var ex = new ExceptionHandler[exCount];
        for (int i = 0; i < ex.Length; i++) ex[i] = new ExceptionHandler(r.U2(), r.U2(), r.U2(), r.U2());
        return new JCodeAttribute { MaxStack = maxStack, MaxLocals = maxLocals, Code = code, ExceptionTable = ex, Attributes = ReadAttributes(r, cp) };
    }

    private static ushort[] ParseExceptions(byte[] raw)
    {
        var r = new BigEndianReader(raw);
        ushort n = r.U2();
        var a = new ushort[n];
        for (int i = 0; i < n; i++) a[i] = r.U2();
        return a;
    }
}