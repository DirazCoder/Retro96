using System.Buffers.Binary;
using System.Text;

namespace Retro96.Engine.Java;

// Class-file parser for major version 45 (JDK 1.0/1.1), minor 0-3.
// Parse failures are reported as ClassFileFormatException so the loader can
// surface them to applets as java.lang.ClassFormatError instead of leaking
// host exceptions through the interpreter.

public sealed class ClassFileFormatException : Exception
{
    public ClassFileFormatException(string message) : base(message) { }
}

public sealed class BigEndianReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _pos;
    public BigEndianReader(ReadOnlyMemory<byte> data) => _data = data;
    public int Position => _pos;
    public bool End => _pos >= _data.Length;
    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos > _data.Length - n) throw new ClassFileFormatException("Unexpected end of class file.");
        var s = _data.Span.Slice(_pos, n);
        _pos += n;
        return s;
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

public sealed record LineNumberEntry(ushort StartPc, ushort LineNumber);

public sealed class JAttribute
{
    public string Name { get; init; } = "";
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    public JCodeAttribute? Code { get; init; }
    public ushort? ConstantValueIndex { get; init; }
    public ushort[]? Exceptions { get; init; }
    public ushort? SourceFileIndex { get; init; }
    public LineNumberEntry[]? LineNumbers { get; init; }
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
    public bool IsStatic => (AccessFlags & 0x0008) != 0;
}

public sealed class JMethod
{
    public ushort AccessFlags { get; init; }
    public string Name { get; init; } = "";
    public string Descriptor { get; init; } = "";
    public JAttribute[] Attributes { get; init; } = Array.Empty<JAttribute>();
    // Class that declares this method; frames resolve constant-pool operands
    // against the declaring class's pool, not against the receiver's class.
    public JClass? DeclaringClass { get; set; }
    private JCodeAttribute? _code;
    private bool _codeResolved;
    public JCodeAttribute? Code
    {
        get
        {
            if (!_codeResolved)
            {
                _code = Attributes.FirstOrDefault(a => a.Code != null)?.Code;
                _codeResolved = true;
            }
            return _code;
        }
    }
    public bool IsStatic => (AccessFlags & 0x0008) != 0;
    public bool IsNative => (AccessFlags & 0x0100) != 0;
    public bool IsAbstract => (AccessFlags & 0x0400) != 0;
    public bool IsSynchronized => (AccessFlags & 0x0020) != 0;
    public bool IsPrivate => (AccessFlags & 0x0002) != 0;
}

// Access flag bits shared by the loader and the interpreter (ACC_SUPER).
public static class JAccess
{
    public const ushort Public = 0x0001;
    public const ushort Private = 0x0002;
    public const ushort Protected = 0x0004;
    public const ushort Static = 0x0008;
    public const ushort Final = 0x0010;
    public const ushort Super = 0x0020;
    public const ushort Interface = 0x0200;
    public const ushort Abstract = 0x0400;
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
        ?? throw new ClassFileFormatException($"Constant #{index} is not Utf8.");

    // Class names come out of the pool with '/' separators; the VM canonical
    // form uses '.', so normalize here once.
    public string ClassNameFromIndex(ushort index)
    {
        if (index == 0 || index >= ConstantPool.Length)
            throw new ClassFileFormatException($"Bad class constant index {index}.");
        var c = ConstantPool[index] as JClassRef ?? throw new ClassFileFormatException("Bad class reference.");
        return Utf8(c.NameIndex).Replace('/', '.');
    }

    public (string Owner, string Name, string Descriptor) Ref(ushort index) => RefValue(index);

    public (string Owner, string Name, string Descriptor) RefValue(ushort index)
    {
        if (index == 0 || index >= ConstantPool.Length)
            throw new ClassFileFormatException($"Bad member reference index {index}.");
        var c = ConstantPool[index] ?? throw new ClassFileFormatException("Constant pool hole used as a member reference.");
        ushort cls, nt;
        switch (c)
        {
            case JFieldRef f: cls = f.ClassIndex; nt = f.NameAndTypeIndex; break;
            case JMethodRef m: cls = m.ClassIndex; nt = m.NameAndTypeIndex; break;
            case JInterfaceMethodRef i: cls = i.ClassIndex; nt = i.NameAndTypeIndex; break;
            default: throw new ClassFileFormatException("Constant is not a member reference.");
        }
        var nameType = ConstantPool[nt] as JNameAndType ?? throw new ClassFileFormatException("Bad NameAndType.");
        return (ClassNameFromIndex(cls), Utf8(nameType.NameIndex), Utf8(nameType.DescriptorIndex));
    }

    public JConstant Constant(ushort index)
    {
        if (index == 0 || index >= ConstantPool.Length)
            throw new ClassFileFormatException($"Constant pool index {index} out of range.");
        return ConstantPool[index]
            ?? throw new ClassFileFormatException("Constant pool index points at a Long/Double hole.");
    }

    public static JClassFile Parse(ReadOnlyMemory<byte> bytes)
    {
        // Zero-length and truncated files must fail with a controlled error,
        // never with a host exception escaping the parser.
        if (bytes.Length == 0) throw new ClassFileFormatException("Class file is empty.");
        JClassFile file;
        try
        {
            file = ParseCore(bytes);
        }
        catch (ClassFileFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ClassFileFormatException($"Malformed class file: {ex.Message}");
        }
        return file;
    }

    private static JClassFile ParseCore(ReadOnlyMemory<byte> bytes)
    {
        var r = new BigEndianReader(bytes);
        if (r.U4() != Magic) throw new ClassFileFormatException("Not a Java class file (bad magic).");
        ushort minor = r.U2(), major = r.U2();
        // Only the authentic JDK 1.0/1.1 range is accepted; later formats
        // have opcode and constant-pool semantics outside this VM's contract.
        if (major != 45 || minor > 3)
            throw new ClassFileFormatException($"Unsupported class version {major}.{minor}; expected 45.0 through 45.3 (JDK 1.0/1.1).");

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
                _ => throw new ClassFileFormatException($"Unsupported constant-pool tag {tag} at #{i}.")
            };
            if (tag is 5 or 6)
            {
                // Long/Double occupy two constant-pool slots; slot i+1 is a
                // hole and must never be dereferenced.
                if (++i >= cpCount) throw new ClassFileFormatException("Long/Double runs past constant pool.");
                cp[i] = null;
            }
        }

        ushort access = r.U2(), thisClass = r.U2(), superClass = r.U2();
        ushort interfacesCount = r.U2();
        var interfaces = new ushort[interfacesCount];
        for (int i = 0; i < interfaces.Length; i++) interfaces[i] = r.U2();

        ushort fieldsCount = r.U2();
        var fields = new JField[fieldsCount];
        for (int i = 0; i < fields.Length; i++)
            fields[i] = new JField
            {
                AccessFlags = r.U2(),
                Name = Utf8(cp, r.U2()),
                Descriptor = Utf8(cp, r.U2()),
                Attributes = ReadAttributes(r, cp)
            };

        ushort methodsCount = r.U2();
        var methods = new JMethod[methodsCount];
        for (int i = 0; i < methods.Length; i++)
            methods[i] = new JMethod
            {
                AccessFlags = r.U2(),
                Name = Utf8(cp, r.U2()),
                Descriptor = Utf8(cp, r.U2()),
                Attributes = ReadAttributes(r, cp)
            };

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

    // Modified UTF-8 (JVMS 4.4.7): NUL is 0xC0 0x80 and supplementary
    // characters arrive as surrogate pairs, each encoded as a 3-byte form.
    private static string DecodeModifiedUtf8(byte[] bytes)
    {
        var chars = new List<char>(bytes.Length);
        for (int i = 0; i < bytes.Length;)
        {
            int b = bytes[i++];
            if (b == 0xC0 && i < bytes.Length && bytes[i] == 0x80) { i++; chars.Add('\0'); continue; }
            if (b < 0x80) { chars.Add((char)b); continue; }
            if ((b & 0xE0) == 0xC0)
            {
                if (i >= bytes.Length) throw new ClassFileFormatException("Truncated modified UTF-8 sequence.");
                int b2 = bytes[i++];
                if ((b2 & 0xC0) != 0x80) throw new ClassFileFormatException("Invalid modified UTF-8 continuation byte.");
                chars.Add((char)(((b & 0x1F) << 6) | (b2 & 0x3F)));
                continue;
            }
            if ((b & 0xF0) == 0xE0)
            {
                if (i + 1 >= bytes.Length) throw new ClassFileFormatException("Truncated modified UTF-8 sequence.");
                int b2 = bytes[i++], b3 = bytes[i++];
                if ((b2 & 0xC0) != 0x80 || (b3 & 0xC0) != 0x80) throw new ClassFileFormatException("Invalid modified UTF-8 continuation byte.");
                chars.Add((char)(((b & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F)));
                continue;
            }
            throw new ClassFileFormatException("Invalid modified UTF-8 byte in class file.");
        }
        return new string(chars.ToArray());
    }

    private static string Utf8(JConstant?[] cp, ushort index) => (cp[index] as JUtf8)?.Value
        ?? throw new ClassFileFormatException("Expected Utf8 constant.");

    private static JAttribute[] ReadAttributes(BigEndianReader r, JConstant?[] cp)
    {
        ushort count = r.U2();
        var result = new JAttribute[count];
        for (int i = 0; i < count; i++)
        {
            string name = Utf8(cp, r.U2());
            uint len = r.U4();
            if (len > int.MaxValue) throw new ClassFileFormatException("Attribute too large.");
            var raw = r.Bytes((int)len);
            result[i] = new JAttribute
            {
                Name = name,
                Raw = raw,
                Code = name == "Code" ? ParseCode(raw, cp) : null,
                ConstantValueIndex = name == "ConstantValue" && raw.Length >= 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(0, 2)) : null,
                Exceptions = name == "Exceptions" ? ParseExceptions(raw) : null,
                SourceFileIndex = name == "SourceFile" && raw.Length >= 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(0, 2)) : null,
                LineNumbers = name == "LineNumberTable" ? ParseLineNumbers(raw) : null
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
        return new JCodeAttribute
        {
            MaxStack = maxStack,
            MaxLocals = maxLocals,
            Code = code,
            ExceptionTable = ex,
            Attributes = ReadAttributes(r, cp)
        };
    }

    private static ushort[] ParseExceptions(byte[] raw)
    {
        var r = new BigEndianReader(raw);
        ushort n = r.U2();
        var a = new ushort[n];
        for (int i = 0; i < n; i++) a[i] = r.U2();
        return a;
    }

    private static LineNumberEntry[] ParseLineNumbers(byte[] raw)
    {
        var r = new BigEndianReader(raw);
        ushort n = r.U2();
        var a = new LineNumberEntry[n];
        for (int i = 0; i < n; i++) a[i] = new LineNumberEntry(r.U2(), r.U2());
        return a;
    }
}
