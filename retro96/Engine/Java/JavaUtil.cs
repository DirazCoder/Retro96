using System.Globalization;

namespace Retro96.Engine.Java;

// Host-side storage for java.util collections.
public sealed class JavaVectorState
{
    public readonly List<JValue> Items = new();
    public int Capacity = 10;
}

// Hashtable storage bucketed by hashCode; equality resolves through the
// VM's virtual equals() so builtin Strings and user classes both work.
public sealed class JavaHashtableState
{
    private readonly Dictionary<int, List<KeyValuePair<JValue, JValue>>> _buckets = new();

    public int Count { get; private set; }

    public JValue? Get(JavaVm vm, JValue key)
    {
        if (TryFind(vm, key, out var bucket, out var index)) return bucket[index].Value;
        return null;
    }

    public JValue? Put(JavaVm vm, JValue key, JValue value)
    {
        if (TryFind(vm, key, out var bucket, out var index))
        {
            var old = bucket[index].Value;
            bucket[index] = new KeyValuePair<JValue, JValue>(key, value);
            return old;
        }
        int h = vm.JavaHashCode(key);
        if (!_buckets.TryGetValue(h, out var list))
        {
            list = new List<KeyValuePair<JValue, JValue>>();
            _buckets[h] = list;
        }
        list.Add(new KeyValuePair<JValue, JValue>(key, value));
        Count++;
        return null;
    }

    public JValue? Remove(JavaVm vm, JValue key)
    {
        if (!TryFind(vm, key, out var bucket, out var index)) return null;
        var old = bucket[index].Value;
        bucket.RemoveAt(index);
        if (bucket.Count == 0) _buckets.Remove(vm.JavaHashCode(key));
        Count--;
        return old;
    }

    public bool ContainsKey(JavaVm vm, JValue key) => TryFind(vm, key, out _, out _);

    public bool ContainsValue(JavaVm vm, JValue value)
    {
        foreach (var bucket in _buckets.Values)
            foreach (var kv in bucket)
                if (vm.JavaEquals(kv.Value, value)) return true;
        return false;
    }

    public List<JValue> Keys()
    {
        var keys = new List<JValue>();
        foreach (var bucket in _buckets.Values)
            foreach (var kv in bucket) keys.Add(kv.Key);
        return keys;
    }

    public List<JValue> Values()
    {
        var values = new List<JValue>();
        foreach (var bucket in _buckets.Values)
            foreach (var kv in bucket) values.Add(kv.Value);
        return values;
    }

    private bool TryFind(JavaVm vm, JValue key, out List<KeyValuePair<JValue, JValue>> bucket, out int index)
    {
        int h = vm.JavaHashCode(key);
        if (_buckets.TryGetValue(h, out bucket!))
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (vm.JavaEquals(bucket[i].Key, key))
                {
                    index = i;
                    return true;
                }
            }
        }
        index = -1;
        return false;
    }
}

public sealed class JavaEnumerationState
{
    private readonly List<JValue> _items;
    private int _position;

    public JavaEnumerationState(List<JValue> items) => _items = items;

    public bool HasMore() => _position < _items.Count;

    public JValue Next() => _items[_position++];
}

// Natives for java.util: Vector, Stack, Hashtable, Enumeration, Random, Date.
internal static class JavaUtil
{
    public static void Register(JavaVm vm, JClass c)
    {
        switch (c.Name)
        {
            case "java.util.Vector": RegisterVector(vm, c); break;
            case "java.util.Stack": RegisterStack(vm, c); break;
            case "java.util.Hashtable": RegisterHashtable(vm, c); break;
            case "java.util.Enumeration": RegisterEnumeration(vm, c); break;
            case "java.util.Random": RegisterRandom(vm, c); break;
            case "java.util.Date": RegisterDate(vm, c); break;
        }
    }

    private static JavaVectorState VectorState(JObject o) => (JavaVectorState)(o.NativeState ??= new JavaVectorState());

    private static void RegisterVector(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { _ = VectorState(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(I)V", i => { var s = VectorState(i.Receiver.AsObject()!); s.Capacity = Math.Max(0, i.Arguments[0].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(II)V", i => { var s = VectorState(i.Receiver.AsObject()!); s.Capacity = Math.Max(0, i.Arguments[0].AsInt()); return JValue.Void; });
        vm.RegisterNative(c.Name, "addElement", "(Ljava/lang/Object;)V", i => { AddElement(vm, i.Receiver.AsObject()!, i.Arguments[0]); return JValue.Void; });
        vm.RegisterNative(c.Name, "add", "(Ljava/lang/Object;)Z", i => { AddElement(vm, i.Receiver.AsObject()!, i.Arguments[0]); return JValue.Int(1); });
        vm.RegisterNative(c.Name, "elementAt", "(I)Ljava/lang/Object;", i => ElementAt(vm, i.Receiver.AsObject()!, i.Arguments[0].AsInt()));
        vm.RegisterNative(c.Name, "get", "(I)Ljava/lang/Object;", i => ElementAt(vm, i.Receiver.AsObject()!, i.Arguments[0].AsInt()));
        vm.RegisterNative(c.Name, "firstElement", "()Ljava/lang/Object;", i => ElementAt(vm, i.Receiver.AsObject()!, 0));
        vm.RegisterNative(c.Name, "lastElement", "()Ljava/lang/Object;", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            return ElementAt(vm, i.Receiver.AsObject()!, s.Items.Count - 1);
        });
        vm.RegisterNative(c.Name, "setElementAt", "(Ljava/lang/Object;I)V", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            int idx = i.Arguments[1].AsInt();
            if ((uint)idx >= (uint)s.Items.Count)
                throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "index " + idx), 0);
            s.Items[idx] = i.Arguments[0];
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "set", "(ILjava/lang/Object;)Ljava/lang/Object;", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            int idx = i.Arguments[0].AsInt();
            if ((uint)idx >= (uint)s.Items.Count)
                throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "index " + idx), 0);
            var old = s.Items[idx];
            s.Items[idx] = i.Arguments[1];
            return old;
        });
        vm.RegisterNative(c.Name, "insertElementAt", "(Ljava/lang/Object;I)V", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            int idx = i.Arguments[1].AsInt();
            if (idx < 0 || idx > s.Items.Count)
                throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "index " + idx), 0);
            s.Items.Insert(idx, i.Arguments[0]);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "removeElement", "(Ljava/lang/Object;)Z", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            for (int k = 0; k < s.Items.Count; k++)
            {
                if (vm.JavaEquals(s.Items[k], i.Arguments[0]))
                {
                    s.Items.RemoveAt(k);
                    return JValue.Int(1);
                }
            }
            return JValue.Int(0);
        });
        vm.RegisterNative(c.Name, "remove", "(I)Ljava/lang/Object;", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            int idx = i.Arguments[0].AsInt();
            if ((uint)idx >= (uint)s.Items.Count)
                throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "index " + idx), 0);
            var old = s.Items[idx];
            s.Items.RemoveAt(idx);
            return old;
        });
        vm.RegisterNative(c.Name, "removeElementAt", "(I)V", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            int idx = i.Arguments[0].AsInt();
            if ((uint)idx >= (uint)s.Items.Count)
                throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "index " + idx), 0);
            s.Items.RemoveAt(idx);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "removeAllElements", "()V", i => { VectorState(i.Receiver.AsObject()!).Items.Clear(); return JValue.Void; });
        vm.RegisterNative(c.Name, "clear", "()V", i => { VectorState(i.Receiver.AsObject()!).Items.Clear(); return JValue.Void; });
        vm.RegisterNative(c.Name, "size", "()I", i => JValue.Int(VectorState(i.Receiver.AsObject()!).Items.Count));
        vm.RegisterNative(c.Name, "isEmpty", "()Z", i => JValue.Int(VectorState(i.Receiver.AsObject()!).Items.Count == 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "contains", "(Ljava/lang/Object;)Z", i =>
            JValue.Int(VectorState(i.Receiver.AsObject()!).Items.Any(item => vm.JavaEquals(item, i.Arguments[0])) ? 1 : 0));
        vm.RegisterNative(c.Name, "indexOf", "(Ljava/lang/Object;)I", i => JValue.Int(IndexOf(vm, i.Receiver.AsObject()!, i.Arguments[0], 0)));
        vm.RegisterNative(c.Name, "indexOf", "(Ljava/lang/Object;I)I", i => JValue.Int(IndexOf(vm, i.Receiver.AsObject()!, i.Arguments[0], i.Arguments[1].AsInt())));
        vm.RegisterNative(c.Name, "elements", "()Ljava/util/Enumeration;", i =>
            JValue.Ref(MakeEnumeration(vm, VectorState(i.Receiver.AsObject()!).Items.ToList())));
        vm.RegisterNative(c.Name, "copyInto", "([Ljava/lang/Object;)V", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            var a = i.Arguments[0].AsArray() ?? throw new JvmException(vm.CreateExceptionObject("java.lang.NullPointerException"), 0);
            if (a.Elements.Length < s.Items.Count)
                throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "destination too small"), 0);
            for (int k = 0; k < s.Items.Count; k++) a.Elements[k] = s.Items[k];
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "toArray", "()[Ljava/lang/Object;", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            var arr = vm.NewArray("Ljava.lang.Object;", s.Items.Count);
            for (int k = 0; k < s.Items.Count; k++) arr.Elements[k] = s.Items[k];
            return JValue.Ref(arr);
        });
        vm.RegisterNative(c.Name, "capacity", "()I", i => JValue.Int(VectorState(i.Receiver.AsObject()!).Capacity));
        vm.RegisterNative(c.Name, "ensureCapacity", "(I)V", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            while (s.Capacity < i.Arguments[0].AsInt()) s.Capacity = s.Capacity == 0 ? 10 : s.Capacity * 2;
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "setSize", "(I)V", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            int n = Math.Max(0, i.Arguments[0].AsInt());
            while (s.Items.Count < n) s.Items.Add(JValue.Ref(null));
            if (s.Items.Count > n) s.Items.RemoveRange(n, s.Items.Count - n);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            return JValue.Ref(vm.CreateString("[" + string.Join(", ", s.Items.Select(v => vm.ToJavaString(v))) + "]"));
        });
    }

    private static void AddElement(JavaVm vm, JObject o, JValue value)
    {
        var s = VectorState(o);
        s.Items.Add(value);
        // Capacity doubles on overflow, matching 1.1 growth behaviour.
        if (s.Items.Count > s.Capacity) s.Capacity = s.Capacity == 0 ? 10 : s.Capacity * 2;
    }

    private static JValue ElementAt(JavaVm vm, JObject o, int idx)
    {
        var s = VectorState(o);
        if ((uint)idx >= (uint)s.Items.Count)
            throw new JvmException(vm.CreateExceptionObject("java.lang.ArrayIndexOutOfBoundsException", "index " + idx + " >= " + s.Items.Count), 0);
        return s.Items[idx];
    }

    private static int IndexOf(JavaVm vm, JObject o, JValue value, int from)
    {
        var s = VectorState(o);
        for (int k = Math.Max(0, from); k < s.Items.Count; k++)
            if (vm.JavaEquals(s.Items[k], value)) return k;
        return -1;
    }

    private static void RegisterStack(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { _ = VectorState(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "push", "(Ljava/lang/Object;)Ljava/lang/Object;", i =>
        {
            AddElement(vm, i.Receiver.AsObject()!, i.Arguments[0]);
            return i.Arguments[0];
        });
        vm.RegisterNative(c.Name, "pop", "()Ljava/lang/Object;", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            if (s.Items.Count == 0)
                throw new JvmException(vm.CreateExceptionObject("java.util.EmptyStackException"), 0);
            var top = s.Items[^1];
            s.Items.RemoveAt(s.Items.Count - 1);
            return top;
        });
        vm.RegisterNative(c.Name, "peek", "()Ljava/lang/Object;", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            if (s.Items.Count == 0)
                throw new JvmException(vm.CreateExceptionObject("java.util.EmptyStackException"), 0);
            return s.Items[^1];
        });
        vm.RegisterNative(c.Name, "empty", "()Z", i => JValue.Int(VectorState(i.Receiver.AsObject()!).Items.Count == 0 ? 1 : 0));
        // search returns the 1-based distance from the top, -1 when absent.
        vm.RegisterNative(c.Name, "search", "(Ljava/lang/Object;)I", i =>
        {
            var s = VectorState(i.Receiver.AsObject()!);
            for (int pos = 1; pos <= s.Items.Count; pos++)
                if (vm.JavaEquals(s.Items[^pos], i.Arguments[0])) return JValue.Int(pos);
            return JValue.Int(-1);
        });
    }

    private static JavaHashtableState HashtableState(JObject o) => (JavaHashtableState)(o.NativeState ??= new JavaHashtableState());

    private static void RegisterHashtable(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { _ = HashtableState(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(I)V", i => { _ = HashtableState(i.Receiver.AsObject()!); return JValue.Void; });
        // Keys and values must both be non-null.
        vm.RegisterNative(c.Name, "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", i =>
        {
            if (i.Arguments[0].AsReference() == null || i.Arguments[1].AsReference() == null)
                throw new JvmException(vm.CreateExceptionObject("java.lang.NullPointerException"), 0);
            var old = HashtableState(i.Receiver.AsObject()!).Put(vm, i.Arguments[0], i.Arguments[1]);
            return old ?? JValue.Ref(null);
        });
        vm.RegisterNative(c.Name, "get", "(Ljava/lang/Object;)Ljava/lang/Object;", i =>
        {
            if (i.Arguments[0].AsReference() == null) return JValue.Ref(null);
            var found = HashtableState(i.Receiver.AsObject()!).Get(vm, i.Arguments[0]);
            return found ?? JValue.Ref(null);
        });
        vm.RegisterNative(c.Name, "remove", "(Ljava/lang/Object;)Ljava/lang/Object;", i =>
        {
            if (i.Arguments[0].AsReference() == null) return JValue.Ref(null);
            var old = HashtableState(i.Receiver.AsObject()!).Remove(vm, i.Arguments[0]);
            return old ?? JValue.Ref(null);
        });
        vm.RegisterNative(c.Name, "containsKey", "(Ljava/lang/Object;)Z", i =>
        {
            if (i.Arguments[0].AsReference() == null) return JValue.Int(0);
            return JValue.Int(HashtableState(i.Receiver.AsObject()!).ContainsKey(vm, i.Arguments[0]) ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "contains", "(Ljava/lang/Object;)Z", i =>
        {
            var s = HashtableState(i.Receiver.AsObject()!);
            return JValue.Int(i.Arguments[0].AsReference() != null && s.ContainsValue(vm, i.Arguments[0]) ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "containsValue", "(Ljava/lang/Object;)Z", i =>
        {
            var s = HashtableState(i.Receiver.AsObject()!);
            return JValue.Int(i.Arguments[0].AsReference() != null && s.ContainsValue(vm, i.Arguments[0]) ? 1 : 0);
        });
        vm.RegisterNative(c.Name, "size", "()I", i => JValue.Int(HashtableState(i.Receiver.AsObject()!).Count));
        vm.RegisterNative(c.Name, "isEmpty", "()Z", i => JValue.Int(HashtableState(i.Receiver.AsObject()!).Count == 0 ? 1 : 0));
        vm.RegisterNative(c.Name, "clear", "()V", i =>
        {
            var s = HashtableState(i.Receiver.AsObject()!);
            foreach (var key in s.Keys().ToArray()) s.Remove(vm, key);
            return JValue.Void;
        });
        vm.RegisterNative(c.Name, "keys", "()Ljava/util/Enumeration;", i =>
            JValue.Ref(MakeEnumeration(vm, HashtableState(i.Receiver.AsObject()!).Keys())));
        vm.RegisterNative(c.Name, "elements", "()Ljava/util/Enumeration;", i =>
            JValue.Ref(MakeEnumeration(vm, HashtableState(i.Receiver.AsObject()!).Values())));
    }

    // Host-backed Enumeration objects. User classes implementing the
    // interface dispatch to their own methods instead.
    internal static JObject MakeEnumeration(JavaVm vm, List<JValue> items)
    {
        var cls = vm.LoadClass("java.util.Enumeration");
        return new JObject { Class = cls, NativeState = new JavaEnumerationState(items) };
    }

    private static void RegisterEnumeration(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "hasMoreElements", "()Z", i =>
            JValue.Int((i.Receiver.AsObject()?.NativeState as JavaEnumerationState)?.HasMore() == true ? 1 : 0));
        vm.RegisterNative(c.Name, "nextElement", "()Ljava/lang/Object;", i =>
        {
            var st = i.Receiver.AsObject()?.NativeState as JavaEnumerationState
                ?? throw new JvmException(vm.CreateExceptionObject("java.util.NoSuchElementException"), 0);
            if (!st.HasMore())
                throw new JvmException(vm.CreateExceptionObject("java.util.NoSuchElementException"), 0);
            return st.Next();
        });
    }

    // java.util.Random with the JDK LCG so equal seeds produce equal
    // sequences and default seeds never collide within a millisecond.
    private sealed class JavaRandomState
    {
        private long _seed;
        private bool _haveNextGaussian;
        private double _nextGaussianValue;
        private static long _uniquifier;

        public JavaRandomState()
        {
            long nanos = DateTime.UtcNow.Ticks * 100;
            long counter = Interlocked.Increment(ref _uniquifier);
            Seed(unchecked(nanos ^ counter * (long)0x9E3779B97F4A7C15UL));
        }

        public JavaRandomState(long seed) => Seed(seed);

        public void Seed(long seed) => _seed = (seed ^ 0x5DEECE66D) & ((1L << 48) - 1);

        private int Next(int bits)
        {
            _seed = (_seed * 0x5DEECE66D + 0xB) & ((1L << 48) - 1);
            return unchecked((int)(long)((ulong)_seed >> (48 - bits)));
        }

        public int NextInt() => Next(32);

        public int NextInt(int bound)
        {
            if (bound <= 0) return int.MinValue; // caller raises IllegalArgumentException
            if ((bound & -bound) == bound) // power of two
                return (int)((bound * (long)Next(31)) >> 31);
            int bits, value;
            do
            {
                bits = Next(31);
                value = bits % bound;
            } while (bits - value + (bound - 1) < 0);
            return value;
        }

        public long NextLong() => ((long)Next(32) << 32) + Next(32);

        public float NextFloat() => Next(24) / (float)(1 << 24);

        public double NextDouble() => (((long)Next(26) << 27) + Next(27)) / (double)(1L << 53);

        public bool NextBoolean() => Next(1) != 0;

        public double NextGaussian()
        {
            if (_haveNextGaussian)
            {
                _haveNextGaussian = false;
                return _nextGaussianValue;
            }
            double v1, v2, s;
            do
            {
                v1 = 2.0 * NextDouble() - 1.0;
                v2 = 2.0 * NextDouble() - 1.0;
                s = v1 * v1 + v2 * v2;
            } while (s >= 1.0 || s == 0.0);
            double multiplier = Math.Sqrt(-2.0 * Math.Log(s) / s);
            _nextGaussianValue = v2 * multiplier;
            _haveNextGaussian = true;
            return v1 * multiplier;
        }
    }

    private static JavaRandomState RandomState(JObject o) => (JavaRandomState)(o.NativeState ??= new JavaRandomState());

    private static void RegisterRandom(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { _ = RandomState(i.Receiver.AsObject()!); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(J)V", i => { var o = i.Receiver.AsObject()!; o.NativeState = new JavaRandomState(i.Arguments[0].AsLong()); return JValue.Void; });
        vm.RegisterNative(c.Name, "setSeed", "(J)V", i => { RandomState(i.Receiver.AsObject()!).Seed(i.Arguments[0].AsLong()); return JValue.Void; });
        vm.RegisterNative(c.Name, "nextInt", "()I", i => JValue.Int(RandomState(i.Receiver.AsObject()!).NextInt()));
        vm.RegisterNative(c.Name, "nextInt", "(I)I", i =>
        {
            int bound = i.Arguments[0].AsInt();
            if (bound <= 0)
                throw new JvmException(vm.CreateExceptionObject("java.lang.IllegalArgumentException", "bound must be positive"), 0);
            return JValue.Int(RandomState(i.Receiver.AsObject()!).NextInt(bound));
        });
        vm.RegisterNative(c.Name, "nextLong", "()J", i => JValue.Long(RandomState(i.Receiver.AsObject()!).NextLong()));
        vm.RegisterNative(c.Name, "nextDouble", "()D", i => JValue.Double(RandomState(i.Receiver.AsObject()!).NextDouble()));
        vm.RegisterNative(c.Name, "nextFloat", "()F", i => JValue.Float(RandomState(i.Receiver.AsObject()!).NextFloat()));
        vm.RegisterNative(c.Name, "nextBoolean", "()Z", i => JValue.Int(RandomState(i.Receiver.AsObject()!).NextBoolean() ? 1 : 0));
        vm.RegisterNative(c.Name, "nextGaussian", "()D", i => JValue.Double(RandomState(i.Receiver.AsObject()!).NextGaussian()));
    }

    private static void RegisterDate(JavaVm vm, JClass c)
    {
        vm.RegisterNative(c.Name, "<init>", "()V", i => { i.Receiver.AsObject()!.NativeState = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); return JValue.Void; });
        vm.RegisterNative(c.Name, "<init>", "(J)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsLong(); return JValue.Void; });
        vm.RegisterNative(c.Name, "getTime", "()J", i => JValue.Long(i.Receiver.AsObject()?.NativeState is long v ? v : 0L));
        vm.RegisterNative(c.Name, "setTime", "(J)V", i => { i.Receiver.AsObject()!.NativeState = i.Arguments[0].AsLong(); return JValue.Void; });
        vm.RegisterNative(c.Name, "before", "(Ljava/util/Date;)Z", i => JValue.Int(TimeOf(i.Receiver) < TimeOf(i.Arguments[0]) ? 1 : 0));
        vm.RegisterNative(c.Name, "after", "(Ljava/util/Date;)Z", i => JValue.Int(TimeOf(i.Receiver) > TimeOf(i.Arguments[0]) ? 1 : 0));
        vm.RegisterNative(c.Name, "equals", "(Ljava/lang/Object;)Z", i =>
            JValue.Int(i.Arguments[0].AsObject()?.NativeState is long other && TimeOf(i.Receiver) == other ? 1 : 0));
        vm.RegisterNative(c.Name, "hashCode", "()I", i =>
        {
            long t = TimeOf(i.Receiver);
            return JValue.Int(unchecked((int)(t >> 32) ^ (int)t));
        });
        // Deprecated accessors kept for applets that still call them.
        vm.RegisterNative(c.Name, "getYear", "()I", i => JValue.Int(At(i.Receiver).Year - 1900));
        vm.RegisterNative(c.Name, "getMonth", "()I", i => JValue.Int(At(i.Receiver).Month - 1));
        vm.RegisterNative(c.Name, "getDay", "()I", i => JValue.Int((int)At(i.Receiver).DayOfWeek));
        vm.RegisterNative(c.Name, "getDate", "()I", i => JValue.Int(At(i.Receiver).Day));
        vm.RegisterNative(c.Name, "getHours", "()I", i => JValue.Int(At(i.Receiver).Hour));
        vm.RegisterNative(c.Name, "getMinutes", "()I", i => JValue.Int(At(i.Receiver).Minute));
        vm.RegisterNative(c.Name, "getSeconds", "()I", i => JValue.Int(At(i.Receiver).Second));
        vm.RegisterNative(c.Name, "toString", "()Ljava/lang/String;", i =>
            JValue.Ref(vm.CreateString(At(i.Receiver).ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture))));
    }

    private static long TimeOf(JValue v) => v.AsObject()?.NativeState is long t ? t : 0L;

    private static DateTimeOffset At(JValue v) => DateTimeOffset.FromUnixTimeMilliseconds(TimeOf(v));
}
