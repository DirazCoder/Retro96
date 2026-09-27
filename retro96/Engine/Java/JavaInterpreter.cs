using System.IO;

namespace Retro96.Engine.Java;

public sealed partial class JavaVm
{
    // =====================================================================
    // Bytecode interpreter for class files 45.0–45.3 (JDK 1.0 / 1.1).
    //
    // Value model: category-2 values (long/double) occupy ONE operand-stack
    // slot (JValue carries all 64 bits) but TWO local-variable slots, which
    // is the layout MaxLocals/MaxStack in the Code attribute are computed
    // against.
    //
    // Frame ownership (FIX): a frame's Owner is the class that DECLARES the
    // method, not the class the call was resolved through. Inherited methods
    // must resolve constant-pool operands against their own class file; the
    // original resolved them against the subclass, reading the wrong pool.
    // =====================================================================

    private JValue Interpret(JavaFrame f)
    {
        var code = f.Method.Code?.Code ?? Array.Empty<byte>();
        while (f.Pc >= 0 && f.Pc < code.Length)
        {
            f.LastOpcodePc = f.Pc;
            byte op = code[f.Pc++];
            try
            {
                switch (op)
                {
                    // ---------------- constants ----------------
                    case 0x00: break;                                              // nop
                    case 0x01: f.Push(JValue.Ref(null)); break;                    // aconst_null
                    case >= 0x02 and <= 0x08: f.Push(JValue.Int(op - 0x03)); break; // iconst_m1..iconst_5
                    case 0x09: f.Push(JValue.Long(0)); break;                      // lconst_0
                    case 0x0A: f.Push(JValue.Long(1)); break;                      // lconst_1
                    case 0x0B: f.Push(JValue.Float(0)); break;                     // fconst_0
                    case 0x0C: f.Push(JValue.Float(1)); break;                     // fconst_1
                    case 0x0D: f.Push(JValue.Float(2)); break;                     // fconst_2
                    case 0x0E: f.Push(JValue.Double(0)); break;                    // dconst_0
                    case 0x0F: f.Push(JValue.Double(1)); break;                    // dconst_1
                    case 0x10: f.Push(JValue.Int(unchecked((sbyte)U1(code, ref f.Pc)))); break; // bipush
                    case 0x11: f.Push(JValue.Int(S2(code, ref f.Pc))); break;      // sipush
                    case 0x12: f.Push(GetConstant(f.Owner.File!, U1(code, ref f.Pc))); break; // ldc
                    case 0x13: f.Push(GetConstant(f.Owner.File!, U2(code, ref f.Pc))); break; // ldc_w
                    case 0x14: f.Push(GetConstant(f.Owner.File!, U2(code, ref f.Pc))); break; // ldc2_w

                    // ---------------- local variable loads ----------------
                    case 0x15 or 0x16 or 0x17 or 0x18 or 0x19: f.Push(Local(f, U1(code, ref f.Pc))); break;
                    case >= 0x1A and <= 0x1D: f.Push(Local(f, op - 0x1A)); break; // iload_n
                    case >= 0x1E and <= 0x21: f.Push(Local(f, op - 0x1E)); break; // lload_n
                    case >= 0x22 and <= 0x25: f.Push(Local(f, op - 0x22)); break; // fload_n
                    case >= 0x26 and <= 0x29: f.Push(Local(f, op - 0x26)); break; // dload_n
                    case >= 0x2A and <= 0x2D: f.Push(Local(f, op - 0x2A)); break; // aload_n

                    // ---------------- array loads ----------------
                    // (baload/caload/saload need no narrowing here: elements
                    // were narrowed at STORE time — see ArrayStore.)
                    case >= 0x2E and <= 0x35: ArrayLoad(f); break;

                    // ---------------- local variable stores ----------------
                    case 0x36 or 0x37 or 0x38 or 0x39 or 0x3A: StoreLocal(f, U1(code, ref f.Pc), f.Pop()); break;
                    case >= 0x3B and <= 0x3E: StoreLocal(f, op - 0x3B, f.Pop()); break; // istore_n
                    case >= 0x3F and <= 0x42: StoreLocal(f, op - 0x3F, f.Pop()); break; // lstore_n
                    case >= 0x43 and <= 0x46: StoreLocal(f, op - 0x43, f.Pop()); break; // fstore_n
                    case >= 0x47 and <= 0x4A: StoreLocal(f, op - 0x47, f.Pop()); break; // dstore_n
                    case >= 0x4B and <= 0x4E: StoreLocal(f, op - 0x4B, f.Pop()); break; // astore_n

                    // ---------------- array stores ----------------
                    case >= 0x4F and <= 0x56: ArrayStore(f, op); break;

                    // ---------------- stack ops ----------------
                    case 0x57: _ = f.Pop(); break; // pop
                    case 0x58: // pop2 — FIX: also handles two category-1 values
                    {
                        var a = f.Pop();
                        if (a.Tag is not (JTag.Long or JTag.Double)) _ = f.Pop();
                        break;
                    }
                    case 0x59: { var v = f.Peek(); f.Push(v); break; } // dup
                    case 0x5A: { var v1 = f.Pop(); var v2 = f.Pop(); f.Push(v1); f.Push(v2); f.Push(v1); break; } // dup_x1
                    case 0x5B: { var a = f.Pop(); var b = f.Pop(); f.Push(a); f.Push(b); f.Push(a); break; } // dup_x2
                    case 0x5C: // dup2 — FIX: category-2 aware
                    {
                        var a = f.Pop();
                        if (a.Tag is JTag.Long or JTag.Double) { f.Push(a); f.Push(a); }
                        else { var b = f.Pop(); f.Push(b); f.Push(a); f.Push(b); f.Push(a); }
                        break;
                    }
                    case 0x5D: // dup2_x1 — FIX: original produced dup2's result
                    {
                        var a = f.Pop();
                        if (a.Tag is JTag.Long or JTag.Double)
                        {
                            var b = f.Pop(); f.Push(a); f.Push(b); f.Push(a);
                        }
                        else
                        {
                            var b = f.Pop(); var c = f.Pop();
                            f.Push(b); f.Push(a); f.Push(c); f.Push(b); f.Push(a);
                        }
                        break;
                    }
                    case 0x5E: // dup2_x2 — FIX: original produced dup2's result
                    {
                        var a = f.Pop();
                        if (a.Tag is JTag.Long or JTag.Double)
                        {
                            var b = f.Pop();
                            if (b.Tag is JTag.Long or JTag.Double) { f.Push(a); f.Push(b); f.Push(a); }
                            else { var c = f.Pop(); f.Push(a); f.Push(c); f.Push(b); f.Push(a); }
                        }
                        else
                        {
                            var b = f.Pop();
                            var c = f.Pop();
                            if (c.Tag is JTag.Long or JTag.Double) { f.Push(b); f.Push(a); f.Push(c); f.Push(b); f.Push(a); }
                            else { var d = f.Pop(); f.Push(b); f.Push(a); f.Push(d); f.Push(c); f.Push(b); f.Push(a); }
                        }
                        break;
                    }
                    case 0x5F: { var a = f.Pop(); var b = f.Pop(); f.Push(a); f.Push(b); break; } // swap

                    // ---------------- int arithmetic ----------------
                    case 0x60: BinInt(f, (a, b) => a + b); break;   // iadd
                    case 0x64: BinInt(f, (a, b) => a - b); break;   // isub
                    case 0x68: BinInt(f, (a, b) => a * b); break;   // imul
                    case 0x6C: IntDiv(f); break;                    // idiv
                    case 0x70: IntRem(f); break;                    // irem
                    case 0x74: f.Push(JValue.Int(unchecked(-f.Pop().AsInt()))); break; // ineg
                    case 0x78: ShiftInt(f, (a, s) => a << (s & 0x1F)); break;            // ishl
                    case 0x7A: ShiftInt(f, (a, s) => (int)((uint)a >> (s & 0x1F))); break; // iushr
                    case 0x7C: ShiftInt(f, (a, s) => a >> (s & 0x1F)); break;            // ishr
                    case 0x7E: BinInt(f, (a, b) => a & b); break;   // iand
                    case 0x80: BinInt(f, (a, b) => a | b); break;   // ior
                    case 0x82: BinInt(f, (a, b) => a ^ b); break;   // ixor

                    // ---------------- long arithmetic ----------------
                    case 0x61: BinLong(f, (a, b) => a + b); break;  // ladd
                    case 0x65: BinLong(f, (a, b) => a - b); break;  // lsub
                    case 0x69: BinLong(f, (a, b) => a * b); break;  // lmul
                    case 0x6D: LongDiv(f); break;                   // ldiv
                    case 0x71: LongRem(f); break;                   // lrem
                    case 0x75: f.Push(JValue.Long(unchecked(-f.Pop().AsLong()))); break; // lneg
                    case 0x79: ShiftLong(f, (a, s) => a << (s & 0x3F)); break;             // lshl
                    case 0x7B: ShiftLong(f, (a, s) => (long)((ulong)a >> (s & 0x3F))); break; // lushr
                    case 0x7D: ShiftLong(f, (a, s) => a >> (s & 0x3F)); break;             // lshr
                    case 0x7F: BinLong(f, (a, b) => a & b); break;  // land
                    case 0x81: BinLong(f, (a, b) => a | b); break;  // lor
                    case 0x83: BinLong(f, (a, b) => a ^ b); break;  // lxor

                    // ---------------- float arithmetic ----------------
                    case 0x62: BinFloat(f, (a, b) => a + b); break; // fadd
                    case 0x66: BinFloat(f, (a, b) => a - b); break; // fsub
                    case 0x6A: BinFloat(f, (a, b) => a * b); break; // fmul
                    case 0x6E: BinFloat(f, (a, b) => a / b); break; // fdiv
                    case 0x72: BinFloat(f, (a, b) => a % b); break; // frem
                    case 0x76: f.Push(JValue.Float(-f.Pop().AsFloat())); break; // fneg

                    // ---------------- double arithmetic ----------------
                    case 0x63: BinDouble(f, (a, b) => a + b); break; // dadd
                    case 0x67: BinDouble(f, (a, b) => a - b); break; // dsub
                    case 0x6B: BinDouble(f, (a, b) => a * b); break; // dmul
                    case 0x6F: BinDouble(f, (a, b) => a / b); break; // ddiv
                    case 0x73: BinDouble(f, (a, b) => a % b); break; // drem
                    case 0x77: f.Push(JValue.Double(-f.Pop().AsDouble())); break; // dneg

                    case 0x84: // iinc
                    {
                        int idx = U1(code, ref f.Pc);
                        int delta = unchecked((sbyte)U1(code, ref f.Pc));
                        if (idx >= 0 && idx < f.Locals.Length)
                            f.Locals[idx] = JValue.Int(unchecked(f.Locals[idx].AsInt() + delta));
                        break;
                    }

                    // ---------------- conversions ----------------
                    case 0x85: f.Push(JValue.Long(f.Pop().AsInt())); break;              // i2l
                    case 0x86: f.Push(JValue.Float(f.Pop().AsInt())); break;             // i2f
                    case 0x87: f.Push(JValue.Double(f.Pop().AsInt())); break;            // i2d
                    case 0x88: f.Push(JValue.Int(unchecked((int)f.Pop().AsLong()))); break; // l2i
                    case 0x89: f.Push(JValue.Float(f.Pop().AsLong())); break;            // l2f
                    case 0x8A: f.Push(JValue.Double(f.Pop().AsLong())); break;           // l2d
                    case 0x8B: f.Push(JValue.Int(FloatToInt(f.Pop().AsFloat()))); break;   // f2i (NaN/Inf clamped — FIX)
                    case 0x8C: f.Push(JValue.Long(FloatToLong(f.Pop().AsFloat()))); break; // f2l
                    case 0x8D: f.Push(JValue.Double(f.Pop().AsFloat())); break;          // f2d
                    case 0x8E: f.Push(JValue.Int(DoubleToInt(f.Pop().AsDouble()))); break;   // d2i (NaN/Inf clamped — FIX)
                    case 0x8F: f.Push(JValue.Long(DoubleToLong(f.Pop().AsDouble()))); break; // d2l
                    case 0x90: f.Push(JValue.Float((float)f.Pop().AsDouble())); break;   // d2f
                    case 0x91: f.Push(JValue.Int(unchecked((sbyte)f.Pop().AsInt()))); break; // i2b (sign-extends — FIX)
                    case 0x92: f.Push(JValue.Int((char)f.Pop().AsInt())); break;         // i2c
                    case 0x93: f.Push(JValue.Int(unchecked((short)f.Pop().AsInt()))); break; // i2s

                    // ---------------- comparisons ----------------
                    case 0x94: CompareLong(f); break;                          // lcmp
                    case 0x95: CompareFloat(f, nanGreater: false); break;      // fcmpl
                    case 0x96: CompareFloat(f, nanGreater: true); break;       // fcmpg
                    case 0x97: CompareDouble(f, nanGreater: false); break;     // dcmpl
                    case 0x98: CompareDouble(f, nanGreater: true); break;      // dcmpg

                    // ---------------- branches ----------------
                    case >= 0x99 and <= 0x9E: IfZero(f, op, S2(code, ref f.Pc)); break;          // ifeq..ifle
                    case >= 0x9F and <= 0xA4: IfCompare(f, op, S2(code, ref f.Pc)); break;       // if_icmp*
                    case 0xA5: IfRefCompare(f, S2(code, ref f.Pc), branchWhenNotEqual: false); break; // if_acmpeq
                    case 0xA6: IfRefCompare(f, S2(code, ref f.Pc), branchWhenNotEqual: true); break;  // if_acmpne
                    case 0xA7: { short off = S2(code, ref f.Pc); f.Pc = f.LastOpcodePc + off; break; } // goto
                    case 0xA8: { short off = S2(code, ref f.Pc); f.Push(JValue.Int(f.Pc)); f.Pc = f.LastOpcodePc + off; break; } // jsr
                    case 0xA9: f.Pc = Local(f, U1(code, ref f.Pc)).AsInt(); break;               // ret
                    case 0xAA: TableSwitch(f, code); break;
                    case 0xAB: LookupSwitch(f, code); break;

                    // ---------------- returns ----------------
                    case 0xAC or 0xAD or 0xAE or 0xAF or 0xB0: return f.Pop(); // *return
                    case 0xB1: return JValue.Void;                              // return

                    // ---------------- fields ----------------
                    case 0xB2: GetStaticInstruction(f, U2(code, ref f.Pc)); break; // getstatic
                    case 0xB3: PutStaticInstruction(f, U2(code, ref f.Pc)); break; // putstatic
                    case 0xB4: GetFieldInstruction(f, U2(code, ref f.Pc)); break; // getfield
                    case 0xB5: PutFieldInstruction(f, U2(code, ref f.Pc)); break; // putfield

                    // ---------------- invocation ----------------
                    case 0xB6: InvokeInstruction(f, U2(code, ref f.Pc), special: false, isStatic: false); break; // invokevirtual
                    case 0xB7: InvokeInstruction(f, U2(code, ref f.Pc), special: true, isStatic: false); break;  // invokespecial
                    case 0xB8: InvokeInstruction(f, U2(code, ref f.Pc), special: false, isStatic: true); break;  // invokestatic
                    case 0xB9: // invokeinterface (count + 0 operands skipped)
                    {
                        ushort cp = U2(code, ref f.Pc);
                        _ = U1(code, ref f.Pc);
                        _ = U1(code, ref f.Pc);
                        InvokeInstruction(f, cp, special: false, isStatic: false);
                        break;
                    }
                    case 0xBA: throw Fault($"invokedynamic is not valid in class-file 45.x (pc {f.LastOpcodePc}).");

                    // ---------------- objects & arrays ----------------
                    case 0xBB: // new
                    {
                        ushort idx = U2(code, ref f.Pc);
                        var cls = LoadClass(f.Owner.File!.ClassNameFromIndex(idx));
                        f.Push(JValue.Ref(NewObject(cls)));
                        break;
                    }
                    case 0xBC: // newarray
                    {
                        int atype = U1(code, ref f.Pc);
                        int len = f.Pop().AsInt();
                        f.Push(JValue.Ref(NewPrimitiveArray(atype, len)));
                        break;
                    }
                    case 0xBD: // anewarray
                    {
                        ushort idx = U2(code, ref f.Pc);
                        int len = f.Pop().AsInt();
                        f.Push(JValue.Ref(NewReferenceArray(f.Owner.File!.ClassNameFromIndex(idx), len)));
                        break;
                    }
                    case 0xBE: // arraylength
                    {
                        var a = f.Pop().AsArray() ?? throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
                        f.Push(JValue.Int(a.Elements.Length));
                        break;
                    }
                    case 0xBF: ThrowInstruction(f); break; // athrow
                    case 0xC0: CheckCast(f, U2(code, ref f.Pc)); break;
                    case 0xC1: InstanceOf(f, U2(code, ref f.Pc)); break;
                    case 0xC2: // monitorenter
                    {
                        var o = f.Pop().AsReference();
                        if (o == null) throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
                        System.Threading.Monitor.Enter(o);
                        break;
                    }
                    case 0xC3: // monitorexit
                    {
                        var o = f.Pop().AsReference();
                        if (o == null) throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
                        try { System.Threading.Monitor.Exit(o); } catch (SynchronizationLockException) { }
                        break;
                    }
                    case 0xC4: Wide(f, code); break;
                    case 0xC5: // multianewarray
                    {
                        ushort cp = U2(code, ref f.Pc);
                        int dims = U1(code, ref f.Pc);
                        if (dims is <= 0 or > 255) throw Fault("invalid multianewarray dimensions");
                        var counts = new int[dims];
                        for (int i = dims - 1; i >= 0; i--) counts[i] = f.Pop().AsInt();
                        string desc = f.Owner.File!.ClassNameFromIndex(cp);
                        f.Push(JValue.Ref(NewMultiArray(desc, counts, 0)));
                        break;
                    }
                    case 0xC6: IfNull(f, S2(code, ref f.Pc), branchWhenNull: true); break;  // ifnull
                    case 0xC7: IfNull(f, S2(code, ref f.Pc), branchWhenNull: false); break; // ifnonnull
                    case 0xC8: { int off = S4(code, ref f.Pc); f.Pc = f.LastOpcodePc + off; break; } // goto_w
                    case 0xC9: { int off = S4(code, ref f.Pc); f.Push(JValue.Int(f.Pc)); f.Pc = f.LastOpcodePc + off; break; } // jsr_w

                    default:
                        throw Fault($"Unsupported JVM opcode 0x{op:X2} at pc {f.LastOpcodePc} in {f.Owner.Name}.{f.Method.Name}{f.Method.Descriptor}.");
                }
            }
            catch (JvmException ex)
            {
                // Search THIS frame's handler table using the pc of the
                // instruction that faulted (for a propagated exception that is
                // the invoke opcode's pc, per the JVM spec). If no handler
                // matches, HandleException rethrows so the caller frame gets
                // its chance.
                HandleException(f, ex.Object, f.LastOpcodePc);
            }
        }
        return JValue.Void;
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static Exception Fault(string message) => new InvalidOperationException(message);

    private static byte U1(byte[] c, ref int pc) => c[pc++];
    private static ushort U2(byte[] c, ref int pc) { ushort v = (ushort)((c[pc] << 8) | c[pc + 1]); pc += 2; return v; }
    private static short S2(byte[] c, ref int pc) => unchecked((short)U2(c, ref pc));
    private static int S4(byte[] c, ref int pc) { int v = (c[pc] << 24) | (c[pc + 1] << 16) | (c[pc + 2] << 8) | c[pc + 3]; pc += 4; return v; }
    private static int S4At(byte[] c, int p) => (c[p] << 24) | (c[p + 1] << 16) | (c[p + 2] << 8) | c[p + 3];
    private static int Align4(int pc) => (pc + 3) & ~3;

    private static JValue Local(JavaFrame f, int i) => i >= 0 && i < f.Locals.Length ? f.Locals[i] : JValue.Int(0);

    private static void StoreLocal(JavaFrame f, int i, JValue v)
    {
        if (i >= 0 && i < f.Locals.Length) f.Locals[i] = v;
        if (v.Tag is JTag.Long or JTag.Double && i + 1 >= 0 && i + 1 < f.Locals.Length) f.Locals[i + 1] = JValue.Void;
    }

    private static void BinInt(JavaFrame f, Func<int, int, int> op) { int b = f.Pop().AsInt(), a = f.Pop().AsInt(); f.Push(JValue.Int(op(a, b))); }
    private static void BinLong(JavaFrame f, Func<long, long, long> op) { long b = f.Pop().AsLong(), a = f.Pop().AsLong(); f.Push(JValue.Long(op(a, b))); }
    private static void BinFloat(JavaFrame f, Func<float, float, float> op) { float b = f.Pop().AsFloat(), a = f.Pop().AsFloat(); f.Push(JValue.Float(op(a, b))); }
    private static void BinDouble(JavaFrame f, Func<double, double, double> op) { double b = f.Pop().AsDouble(), a = f.Pop().AsDouble(); f.Push(JValue.Double(op(a, b))); }
    private static void ShiftInt(JavaFrame f, Func<int, int, int> op) { int sh = f.Pop().AsInt(), a = f.Pop().AsInt(); f.Push(JValue.Int(op(a, sh))); }
    private static void ShiftLong(JavaFrame f, Func<long, int, long> op) { int sh = f.Pop().AsInt(); long a = f.Pop().AsLong(); f.Push(JValue.Long(op(a, sh))); }

    private void IntDiv(JavaFrame f)
    {
        int b = f.Pop().AsInt(), a = f.Pop().AsInt();
        if (b == 0) throw new JvmException(GetExceptionObject("java.lang.ArithmeticException", "/ by zero"), f.LastOpcodePc);
        f.Push(JValue.Int(unchecked(a / b)));
    }
    private void IntRem(JavaFrame f)
    {
        int b = f.Pop().AsInt(), a = f.Pop().AsInt();
        if (b == 0) throw new JvmException(GetExceptionObject("java.lang.ArithmeticException", "/ by zero"), f.LastOpcodePc);
        f.Push(JValue.Int(unchecked(a % b)));
    }
    private void LongDiv(JavaFrame f)
    {
        long b = f.Pop().AsLong(), a = f.Pop().AsLong();
        if (b == 0) throw new JvmException(GetExceptionObject("java.lang.ArithmeticException", "/ by zero"), f.LastOpcodePc);
        f.Push(JValue.Long(unchecked(a / b)));
    }
    private void LongRem(JavaFrame f)
    {
        long b = f.Pop().AsLong(), a = f.Pop().AsLong();
        if (b == 0) throw new JvmException(GetExceptionObject("java.lang.ArithmeticException", "/ by zero"), f.LastOpcodePc);
        f.Push(JValue.Long(unchecked(a % b)));
    }

    // Java conversion semantics: NaN -> 0, infinities clamp, not undefined.
    private static int FloatToInt(float v)
    {
        if (float.IsNaN(v)) return 0;
        if (v >= int.MaxValue) return int.MaxValue;
        if (v <= int.MinValue) return int.MinValue;
        return (int)v;
    }
    private static long FloatToLong(float v)
    {
        if (float.IsNaN(v)) return 0;
        if (v >= long.MaxValue) return long.MaxValue;
        if (v <= long.MinValue) return long.MinValue;
        return (long)v;
    }
    private static int DoubleToInt(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v >= int.MaxValue) return int.MaxValue;
        if (v <= int.MinValue) return int.MinValue;
        return (int)v;
    }
    private static long DoubleToLong(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v >= long.MaxValue) return long.MaxValue;
        if (v <= long.MinValue) return long.MinValue;
        return (long)v;
    }

    private static void CompareLong(JavaFrame f) { long b = f.Pop().AsLong(), a = f.Pop().AsLong(); f.Push(JValue.Int(a < b ? -1 : a == b ? 0 : 1)); }
    private static void CompareFloat(JavaFrame f, bool nanGreater)
    {
        float b = f.Pop().AsFloat(), a = f.Pop().AsFloat();
        f.Push(JValue.Int(float.IsNaN(a) || float.IsNaN(b) ? (nanGreater ? 1 : -1) : a < b ? -1 : a == b ? 0 : 1));
    }
    private static void CompareDouble(JavaFrame f, bool nanGreater)
    {
        double b = f.Pop().AsDouble(), a = f.Pop().AsDouble();
        f.Push(JValue.Int(double.IsNaN(a) || double.IsNaN(b) ? (nanGreater ? 1 : -1) : a < b ? -1 : a == b ? 0 : 1));
    }

    // ---------------- array access ----------------

    private void ArrayLoad(JavaFrame f)
    {
        int idx = f.Pop().AsInt();
        var a = f.Pop().AsArray() ?? throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
        if ((uint)idx >= (uint)a.Elements.Length)
            throw new JvmException(GetExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "Array index out of range: " + idx), f.LastOpcodePc);
        f.Push(a.Elements[idx]);
    }

    private void ArrayStore(JavaFrame f, byte op)
    {
        JValue v = f.Pop();
        int idx = f.Pop().AsInt();
        var a = f.Pop().AsArray() ?? throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
        if ((uint)idx >= (uint)a.Elements.Length)
            throw new JvmException(GetExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "Array index out of range: " + idx), f.LastOpcodePc);
        switch (op)
        {
            case 0x54: v = JValue.Int(unchecked((sbyte)v.AsInt())); break; // bastore — narrow (FIX)
            case 0x55: v = JValue.Int((char)v.AsInt()); break;             // castore — narrow (FIX)
            case 0x56: v = JValue.Int(unchecked((short)v.AsInt())); break; // sastore — narrow (FIX)
            case 0x53: // aastore — ArrayStoreException check (FIX: the old
                // code called LoadClass() on the raw component DESCRIPTOR,
                // which cannot resolve "Ljava/lang/String;" and always faulted)
                if (v.AsReference() != null && !IsComponentAssignable(a.ComponentDescriptor, v))
                    throw new JvmException(GetExceptionObject("java.lang.ArrayStoreException"), f.LastOpcodePc);
                break;
        }
        a.Elements[idx] = v;
    }

    private bool IsComponentAssignable(string componentDescriptor, JValue value)
    {
        if (componentDescriptor.Length == 0) return true;
        if (componentDescriptor[0] == 'L' && componentDescriptor.EndsWith(";"))
        {
            var target = LoadClass(componentDescriptor[1..^1]); // LoadClass normalizes '/' vs '.'
            return value.AsReference() switch
            {
                JObject jo => jo.Class.IsAssignableTo(target),
                JArray ja => ja.Class.IsAssignableTo(target), // array classes have java.lang.Object as superclass
                _ => false
            };
        }
        if (componentDescriptor[0] == '[')
        {
            // Array-of-arrays: accept an exact (normalized) component match.
            var want = componentDescriptor.Replace('/', '.');
            return value.AsReference() is JArray va && va.ComponentDescriptor.Replace('/', '.') == want;
        }
        return true; // primitive component — well-formed bytecode guarantees the type
    }

    // ---------------- branches & switches ----------------

    private static void IfZero(JavaFrame f, byte op, short off)
    {
        int v = f.Pop().AsInt();
        bool yes = op switch { 0x99 => v == 0, 0x9A => v != 0, 0x9B => v < 0, 0x9C => v >= 0, 0x9D => v > 0, 0x9E => v <= 0, _ => false };
        if (yes) f.Pc = f.LastOpcodePc + off;
    }

    private static void IfCompare(JavaFrame f, byte op, short off)
    {
        int b = f.Pop().AsInt(), a = f.Pop().AsInt();
        bool yes = op switch { 0x9F => a == b, 0xA0 => a != b, 0xA1 => a < b, 0xA2 => a >= b, 0xA3 => a > b, 0xA4 => a <= b, _ => false };
        if (yes) f.Pc = f.LastOpcodePc + off;
    }

    private static void IfRefCompare(JavaFrame f, short off, bool branchWhenNotEqual)
    {
        var b = f.Pop().AsReference();
        var a = f.Pop().AsReference();
        bool same = ReferenceEquals(a, b);
        if (same != branchWhenNotEqual) f.Pc = f.LastOpcodePc + off;
    }

    private static void IfNull(JavaFrame f, short off, bool branchWhenNull)
    {
        var v = f.Pop().AsReference();
        if ((v == null) == branchWhenNull) f.Pc = f.LastOpcodePc + off;
    }

    private static void TableSwitch(JavaFrame f, byte[] code)
    {
        int basePc = f.LastOpcodePc;
        f.Pc = Align4(f.Pc);
        int def = S4(code, ref f.Pc);
        int low = S4(code, ref f.Pc);
        int high = S4(code, ref f.Pc);
        int key = f.Pop().AsInt();
        int off = key < low || key > high ? def : S4At(code, f.Pc + (key - low) * 4);
        f.Pc = basePc + off;
    }

    private static void LookupSwitch(JavaFrame f, byte[] code)
    {
        int basePc = f.LastOpcodePc;
        f.Pc = Align4(f.Pc);
        int def = S4(code, ref f.Pc);
        int npairs = S4(code, ref f.Pc);
        int key = f.Pop().AsInt();
        int off = def;
        for (int i = 0; i < npairs; i++)
        {
            int match = S4(code, ref f.Pc);
            int jump = S4(code, ref f.Pc);
            if (match == key) { off = jump; break; }
        }
        f.Pc = basePc + off;
    }

    // ---------------- fields ----------------

    private void GetStaticInstruction(JavaFrame f, ushort cp)
    {
        var (owner, name, desc) = f.Owner.File!.RefValue(cp);
        f.Push(GetStatic(LoadClass(owner), name, desc));
    }

    private void PutStaticInstruction(JavaFrame f, ushort cp)
    {
        var (owner, name, desc) = f.Owner.File!.RefValue(cp);
        SetStatic(LoadClass(owner), name, desc, f.Pop());
    }

    private void GetFieldInstruction(JavaFrame f, ushort cp)
    {
        var (owner, name, desc) = f.Owner.File!.RefValue(cp);
        var obj = f.Pop().AsObject() ?? throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
        f.Push(GetField(obj, LoadClass(owner), name, desc));
    }

    private void PutFieldInstruction(JavaFrame f, ushort cp)
    {
        var (owner, name, desc) = f.Owner.File!.RefValue(cp);
        var v = f.Pop();
        var obj = f.Pop().AsObject() ?? throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
        SetField(obj, LoadClass(owner), name, desc, v);
    }

    // ---------------- invocation ----------------

    private void InvokeInstruction(JavaFrame f, ushort cp, bool special, bool isStatic)
    {
        var (owner, name, desc) = f.Owner.File!.RefValue(cp);
        var args = PopArgs(f, desc);
        JValue receiver = JValue.Void;
        if (!isStatic)
        {
            receiver = f.Pop();
            // FIX: the original turned a null receiver into MissingMethodException;
            // it must be a NullPointerException.
            if (receiver.AsReference() == null)
                throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
        }
        var target = LoadClass(owner);
        EnsureInitialized(target);
        JMethod? m = isStatic || special
            ? ResolveMethod(target, name, desc)                       // direct resolution
            : ResolveVirtual(receiver.AsObject()!, name, desc);       // vtable walk on the ACTUAL class
        if (m == null)
            throw new JvmException(GetExceptionObject("java.lang.RuntimeException", "NoSuchMethod: " + owner + "." + name + desc), f.LastOpcodePc);
        var result = Invoke(target, m, receiver, args);
        if (result.Tag != JTag.Void) f.Push(result);
    }

    private static JValue[] PopArgs(JavaFrame f, string desc)
    {
        var types = JavaDescriptor.Parse(desc);
        var args = new JValue[types.Count];
        for (int i = types.Count - 1; i >= 0; i--) args[i] = f.Pop();
        return args;
    }

    // ---------------- objects, arrays, casts ----------------

    private void ThrowInstruction(JavaFrame f)
    {
        var obj = f.Pop().AsObject() ?? throw new JvmException(GetExceptionObject("java.lang.NullPointerException"), f.LastOpcodePc);
        throw new JvmException(obj, f.LastOpcodePc);
    }

    private void CheckCast(JavaFrame f, ushort cp)
    {
        var v = f.Pop();
        var o = v.AsReference();
        if (o != null)
        {
            string name = f.Owner.File!.ClassNameFromIndex(cp);
            var cls = o switch { JObject jo => jo.Class, JArray ja => ja.Class, _ => null };
            if (cls == null || !cls.IsAssignableTo(LoadClass(name)))
                throw new JvmException(GetExceptionObject("java.lang.ClassCastException", (cls?.Name ?? "?") + " cannot be cast to " + name), f.LastOpcodePc);
        }
        f.Push(v); // null always passes
    }

    private void InstanceOf(JavaFrame f, ushort cp)
    {
        var o = f.Pop().AsReference();
        string name = f.Owner.File!.ClassNameFromIndex(cp);
        var cls = o switch { JObject jo => jo.Class, JArray ja => ja.Class, _ => null };
        f.Push(JValue.Int(cls != null && cls.IsAssignableTo(LoadClass(name)) ? 1 : 0));
    }

    private void Wide(JavaFrame f, byte[] code)
    {
        byte sub = U1(code, ref f.Pc);
        int idx = U2(code, ref f.Pc);
        switch (sub)
        {
            case 0x15 or 0x16 or 0x17 or 0x18 or 0x19: f.Push(Local(f, idx)); break;
            case 0x36 or 0x37 or 0x38 or 0x39 or 0x3A: StoreLocal(f, idx, f.Pop()); break;
            case 0x84:
            {
                int delta = S2(code, ref f.Pc);
                if (idx >= 0 && idx < f.Locals.Length) f.Locals[idx] = JValue.Int(unchecked(f.Locals[idx].AsInt() + delta));
                break;
            }
            case 0xA9: f.Pc = Local(f, idx).AsInt(); break; // wide ret
            default: throw Fault($"Unsupported wide opcode 0x{sub:X2}");
        }
    }

    private JArray NewPrimitiveArray(int atype, int len) => atype switch
    {
        4 => NewArray("Z", len),
        5 => NewArray("C", len),
        6 => NewArray("F", len),
        7 => NewArray("D", len),
        8 => NewArray("B", len),
        9 => NewArray("S", len),
        10 => NewArray("I", len),
        11 => NewArray("J", len),
        _ => throw Fault("bad newarray type " + atype)
    };

    private JArray NewReferenceArray(string name, int len)
    {
        // FIX: the CP entry may itself be an array type ("[Ljava/lang/Object;")
        // when creating arrays-of-arrays via anewarray.
        string component = name.StartsWith('[') ? name : "L" + name.Replace('.', '/') + ";";
        return NewArray(component, len);
    }

    private JArray NewMultiArray(string descriptor, int[] counts, int level)
    {
        if (level >= counts.Length) throw Fault("multianewarray depth overflow");
        int len = counts[level];
        if (len < 0) throw new JvmException(GetExceptionObject("java.lang.NegativeArraySizeException", len.ToString()), 0);
        var array = NewArray(descriptor[1..], len);
        if (level + 1 < counts.Length)
            for (int i = 0; i < len; i++)
                array.Elements[i] = JValue.Ref(NewMultiArray(descriptor[1..], counts, level + 1));
        return array;
    }

    // ---------------- exceptions ----------------

    private void HandleException(JavaFrame f, JObject obj, int pc)
    {
        var handlers = f.Method.Code?.ExceptionTable ?? Array.Empty<ExceptionHandler>();
        foreach (var h in handlers)
        {
            if (pc < h.StartPc || pc >= h.EndPc) continue;
            if (h.CatchType != 0)
            {
                var catchCls = LoadClass(f.Owner.File!.ClassNameFromIndex(h.CatchType));
                if (!obj.Class.IsAssignableTo(catchCls)) continue;
            }
            f.ClearStack();
            f.Push(JValue.Ref(obj));
            f.Pc = h.HandlerPc;
            return; // handled
        }
        throw new JvmException(obj, pc); // propagate to the caller's frame
    }
}

/// <summary>Parses JVM method descriptors (parameter list / return type).</summary>
public static class JavaDescriptor
{
    public static List<string> Parse(string desc)
    {
        var list = new List<string>();
        if (desc.Length == 0 || desc[0] != '(') return list;
        int p = 1;
        while (p < desc.Length && desc[p] != ')')
        {
            int start = p;
            if (desc[p] == 'L')
            {
                int e = desc.IndexOf(';', p);
                if (e < 0) throw new InvalidDataException("bad descriptor: " + desc);
                p = e + 1;
            }
            else if (desc[p] == '[')
            {
                p++;
                while (p < desc.Length && desc[p] == '[') p++;
                if (p < desc.Length && desc[p] == 'L')
                {
                    int e = desc.IndexOf(';', p);
                    if (e < 0) throw new InvalidDataException("bad descriptor: " + desc);
                    p = e + 1;
                }
                else p++;
            }
            else p++;
            list.Add(desc[start..p]);
        }
        return list;
    }

    public static string Return(string desc)
    {
        int p = desc.IndexOf(')');
        return p < 0 || p + 1 >= desc.Length ? "V" : desc[(p + 1)..];
    }
}