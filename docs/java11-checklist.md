# Java 1.1 Applet Engine — Completeness Checklist

Target: class file major version 45 (JDK 1.0) through 45.3 (JDK 1.1).  
Goal: run real 1996-era applets, not pass a TCK.

---

## 1. Class File Parsing

### Magic & version
- [x] Reject files where magic != `0xCAFEBABE`
- [x] Accept major 45, minor 0–3; reject anything newer with a clear error (not a crash)
- [x] Handle zero-length file and truncated file without throwing a host exception

### Constant pool
- [x] Tag 1 — Utf8: length-prefixed modified UTF-8 (not standard UTF-8); `\u0000` encodes as `0xC0 0x80`, not `0x00`
- [x] Tag 3 — Integer
- [x] Tag 4 — Float: includes NaN and ±Infinity as valid encoded values
- [x] Tag 5 — Long (occupies two CP slots; slot n+1 is unusable)
- [x] Tag 6 — Double (same two-slot rule)
- [x] Tag 7 — Class: name uses `/` as separator, not `.`
- [x] Tag 8 — String: references a Utf8 entry, not an inline string
- [x] Tag 9 — Fieldref
- [x] Tag 10 — Methodref
- [x] Tag 11 — InterfaceMethodref (distinct from Methodref; invokeinterface uses this)
- [x] Tag 12 — NameAndType
- [x] CP index 0 is invalid; valid indices are 1..count-1
- [x] Long/Double at index N makes N+1 inaccessible — don't crash if bytecode reads N+1

### Class metadata
- [x] Access flags (public, final, interface, abstract — super flag matters for invokespecial)
- [x] this_class and super_class as CP Class refs
- [x] interfaces[] array (may be empty)
- [x] Fields: access flags, name, descriptor, attributes (ConstantValue for statics)
- [x] Methods: access flags, name, descriptor, Code attribute
- [x] `<init>` (instance constructor) and `<clinit>` (static initializer) are ordinary methods

### Code attribute
- [x] max_stack and max_locals (use for frame sizing, not enforcement)
- [x] Bytecode array
- [x] Exception table: start_pc (inclusive), end_pc (exclusive), handler_pc, catch_type (0 = catch-all)
- [x] Nested attributes inside Code (LineNumberTable — optional, but useful for stack traces)

---

## 2. Execution Model

### Frames
- [x] Each method call gets its own frame with: operand stack, local variable array, PC, owner class
- [x] Owner = declaring class of the method, not the receiver's runtime class (matters for CP resolution)
- [x] `this` is local variable 0 for instance methods; arg 0 for static methods
- [x] Category-2 values (long, double) occupy ONE stack slot but TWO local variable slots
- [x] Storing a category-2 value at local N must invalidate local N+1 (write `void` sentinel)
- [x] Reading a category-2 local from N+1 directly is illegal bytecode, but don't crash — return a safe default

### Value representation
- [x] All primitive types coerce through int on the stack (byte, short, char are stored as int)
- [x] `boolean` arrays store 0/1 as int; individual boolean values are int on the stack
- [x] NaN bit patterns must round-trip through float/double storage (use bit-level storage, not float arithmetic)
- [x] Negative zero (`-0.0`) is a distinct float/double value; `(-0.0 == 0.0)` is true but they differ under `Float.floatToIntBits`

---

## 3. Bytecode Interpreter

### Constants (0x00–0x14)
- [x] `nop` (0x00)
- [x] `aconst_null` (0x01) — pushes a typed null, not integer 0
- [x] `iconst_m1` through `iconst_5` (0x02–0x08)
- [x] `lconst_0`, `lconst_1` (0x09–0x0A)
- [x] `fconst_0`, `fconst_1`, `fconst_2` (0x0B–0x0D)
- [x] `dconst_0`, `dconst_1` (0x0E–0x0F)
- [x] `bipush` (0x10) — sign-extends byte to int
- [x] `sipush` (0x11) — sign-extends short to int
- [x] `ldc` (0x12) — 1-byte CP index; resolves Integer, Float, or String ref
- [x] `ldc_w` (0x13) — 2-byte CP index, otherwise same
- [x] `ldc2_w` (0x14) — Long or Double only; always 2-byte index

### Local variable loads (0x15–0x2D)
- [x] `iload`, `lload`, `fload`, `dload`, `aload` — indexed form (1-byte operand)
- [x] `iload_0..3`, `lload_0..3`, `fload_0..3`, `dload_0..3`, `aload_0..3` — short forms
- [x] Loading an uninitialized local is undefined; return default value (0 / null), don't crash

### Array loads (0x2E–0x35)
- [x] `iaload`, `laload`, `faload`, `daload`, `aaload`, `baload`, `caload`, `saload`
- [x] NullPointerException if arrayref is null
- [x] ArrayIndexOutOfBoundsException if index < 0 or >= array length
- [x] `baload` sign-extends byte to int
- [x] `caload` zero-extends char to int (char is unsigned 16-bit)
- [x] `saload` sign-extends short to int

### Local variable stores (0x36–0x4E)
- [x] `istore`, `lstore`, `fstore`, `dstore`, `astore` — indexed form
- [x] `istore_0..3`, etc. — short forms
- [x] Storing category-2 at slot N must zero/invalidate slot N+1

### Array stores (0x4F–0x56)
- [x] `iastore`, `lastore`, `fastore`, `dastore`, `aastore`, `bastore`, `castore`, `sastore`
- [x] NullPointerException if arrayref is null
- [x] ArrayIndexOutOfBoundsException if index out of range
- [x] `bastore` narrows int to byte (truncate, then sign-extend for storage)
- [x] `castore` narrows int to char (mask to 16 bits unsigned)
- [x] `sastore` narrows int to short
- [x] `aastore` must check ArrayStoreException: runtime type of value must be assignable to the array's component type — null always passes

### Stack manipulation (0x57–0x5F)
- [x] `pop` (0x57) — removes top of stack; must be a category-1 value in well-formed code
- [x] `pop2` (0x58) — removes one category-2 value OR two category-1 values
- [x] `dup` (0x59) — duplicates top category-1 value
- [x] `dup_x1` (0x5A) — duplicates top, inserts below second
- [x] `dup_x2` (0x5B) — duplicates top, inserts below second+third (or below one category-2)
- [x] `dup2` (0x5C) — duplicates top category-2 value, OR duplicates top two category-1 values
- [x] `dup2_x1` (0x5D) — four forms based on category mix; all four must be correct
- [x] `dup2_x2` (0x5E) — four forms; the hardest instruction to get right
- [x] `swap` (0x5F) — swaps top two category-1 values; illegal on category-2

### Integer arithmetic (0x60–0x84)
- [x] `iadd`, `isub`, `imul` — wrap on overflow (unchecked)
- [x] `idiv` — truncates toward zero; throws ArithmeticException("/ by zero") if divisor is 0
- [x] `irem` — result sign matches dividend; throws ArithmeticException if divisor is 0
- [x] `ineg` — `Integer.MIN_VALUE` negated is still `Integer.MIN_VALUE` (overflow wraps)
- [x] `ishl` — shift amount masked to low 5 bits
- [x] `ishr` — arithmetic (sign-extending) shift right; shift amount masked to 5 bits
- [x] `iushr` — logical (zero-filling) shift right; shift amount masked to 5 bits
- [x] `iand`, `ior`, `ixor`
- [x] `iinc` (0x84) — 1-byte local index, 1-byte signed delta; no overflow check needed

### Long arithmetic (0x61–0x83)
- [x] `ladd`, `lsub`, `lmul` — wrap on overflow
- [x] `ldiv`, `lrem` — same div-by-zero semantics as int; `Long.MIN_VALUE / -1` wraps to `Long.MIN_VALUE`
- [x] `lneg` — `Long.MIN_VALUE` negated stays `Long.MIN_VALUE`
- [x] `lshl`, `lshr`, `lushr` — shift amount is int, masked to low 6 bits (not 5)
- [x] `land`, `lor`, `lxor`
- [x] `lcmp` — pushes -1, 0, or 1

### Float arithmetic (0x62–0x76)
- [x] `fadd`, `fsub`, `fmul`, `fdiv`, `frem`, `fneg`
- [x] NaN propagates: any arithmetic on NaN produces NaN
- [x] Division by zero produces ±Infinity (not an exception)
- [x] `frem` follows IEEE 754 remainder, not Java's `%` for ints — result can be NaN

### Double arithmetic (0x63–0x77)
- [x] Same rules as float arithmetic, extended to 64-bit

### Type conversions (0x85–0x93)
- [x] `i2l` — sign-extend
- [x] `i2f`, `i2d` — may lose precision for large integers
- [x] `l2i` — truncate to low 32 bits (unchecked cast)
- [x] `l2f`, `l2d` — may lose precision
- [x] `f2i` — truncate toward zero; NaN → 0; +Inf → Integer.MAX_VALUE; -Inf → Integer.MIN_VALUE
- [x] `f2l` — same clamping rules with Long bounds
- [x] `f2d` — widening, exact
- [x] `d2i`, `d2l` — same clamping as f2i/f2l
- [x] `d2f` — narrowing; may produce ±Infinity if magnitude too large
- [x] `i2b` — truncate to 8 bits, then sign-extend to int
- [x] `i2c` — mask to 16 bits unsigned (char is 0..65535)
- [x] `i2s` — truncate to 16 bits, then sign-extend to int

### Comparisons (0x94–0x98)
- [x] `lcmp` — no NaN concern; always produces -1/0/1
- [x] `fcmpl` — NaN produces -1
- [x] `fcmpg` — NaN produces +1
- [x] `dcmpl` — NaN produces -1
- [x] `dcmpg` — NaN produces +1

### Branches (0x99–0xA9)
- [x] `ifeq`, `ifne`, `iflt`, `ifge`, `ifgt`, `ifle` — compare int to 0
- [x] `if_icmpeq`, `if_icmpne`, `if_icmplt`, `if_icmpge`, `if_icmpgt`, `if_icmple`
- [x] `if_acmpeq`, `if_acmpne` — reference equality (identity, not `.equals()`)
- [x] `ifnull`, `ifnonnull`
- [x] `goto` — 2-byte signed offset from opcode PC (not from operand PC)
- [x] `goto_w` — 4-byte signed offset
- [x] `jsr` — pushes return address as int, branches; needed for finally blocks compiled by JDK 1.1
- [x] `jsr_w` — same with 4-byte offset
- [x] `ret` — reads return address from local variable, jumps there

### Switches (0xAA–0xAB)
- [x] `tableswitch` — pad to 4-byte alignment after opcode, then default, low, high, offsets
- [x] `lookupswitch` — pad to 4-byte alignment, then default, npairs, sorted match/offset pairs
- [x] Both: offset is relative to the opcode's PC, not the current PC after reading operands
- [x] Both: branch to default if key is out of range / not found
- [x] Alignment is relative to the method's start (PC 0), not file start

### Returns (0xAC–0xB1)
- [x] `ireturn`, `lreturn`, `freturn`, `dreturn`, `areturn` — pop and return top of stack
- [x] `return` (void) — returns JValue.Void
- [x] Return value pushed into caller's frame only if return type is non-void

### Field access (0xB2–0xB5)
- [x] `getstatic` — resolves owner class, initializes it if needed, returns static field value
- [x] `putstatic` — same; triggers class init
- [x] `getfield` — NullPointerException if objectref is null
- [x] `putfield` — NullPointerException if objectref is null
- [x] Field resolution walks superclass chain (field may be inherited)
- [x] CP entry owner may differ from actual declaring class — resolve by walking hierarchy

### Invocation (0xB6–0xB9)
- [x] `invokevirtual` — vtable walk starting at the receiver's actual runtime class
- [x] `invokespecial` — direct resolution; used for constructors, `super.method()`, and private methods; must check ACC_SUPER flag for superclass method calls
- [x] `invokestatic` — no receiver; triggers class initialization
- [x] `invokeinterface` — like invokevirtual but resolves through interface; the count and 0 bytes after the CP index must be consumed
- [x] `invokedynamic` (0xBA) — not valid in class file 45.x; throw a clear error, not a crash
- [x] Null receiver → NullPointerException (before method resolution)
- [x] Method not found → throw java.lang.NoSuchMethodError (as a JVM exception, not a host exception)
- [x] Return value: push only if descriptor return type is not `V`
- [x] Pop args before popping receiver; arg count derived from descriptor

### Objects & arrays (0xBB–0xC5)
- [x] `new` — allocates object, does NOT call `<init>`; the following `invokespecial` does that
- [x] `newarray` — 8 primitive type codes (4=boolean, 5=char, 6=float, 7=double, 8=byte, 9=short, 10=int, 11=long)
- [x] `anewarray` — component may itself be an array type (`[Ljava/lang/Object;`)
- [x] `arraylength` — NullPointerException on null
- [x] `athrow` — NullPointerException if ref is null; clears operand stack, pushes exception, jumps to handler
- [x] `checkcast` — passes for null; ClassCastException otherwise
- [x] `instanceof` — pushes 0 for null (never throws)
- [x] `monitorenter` / `monitorexit` — NullPointerException on null; monitorexit must not throw if the lock isn't held (some compilers generate redundant exits in finally blocks)
- [x] `multianewarray` — NegativeArraySizeException if any dimension count < 0; only allocates as many dimensions as the `dims` operand specifies (not the full descriptor depth)

### Wide prefix (0xC4)
- [x] Extends local variable index to 16 bits for: iload/lload/fload/dload/aload, istore/lstore/fstore/dstore/astore, iinc, ret
- [x] `wide iinc` uses a 2-byte signed delta (vs 1-byte signed in the non-wide form)

---

## 4. Class Loading & Object Model

### Class registry
- [x] Load each class exactly once; cache by name
- [x] Normalize class names: treat `/` and `.` as equivalent separators
- [x] Lazy loading: only load when first referenced (not all at startup)
- [x] Circular dependency during `<clinit>`: mark class as initialized before running its initializer to break the cycle
- [x] Missing class → throw java.lang.NoClassDefFoundError as a JVM exception

### Hierarchy
- [x] Superclass loaded and initialized before subclass `<clinit>` runs
- [x] `java.lang.Object` has no superclass; all other classes ultimately extend it
- [x] Interfaces do not have a superclass in the class hierarchy sense
- [x] Array classes: superclass is `java.lang.Object`; implement `java.lang.Cloneable` and `java.io.Serializable`

### Method resolution
- [x] Virtual dispatch: start at receiver's actual class, walk superclass chain, then interfaces
- [x] Interface dispatch: check class hierarchy first, then all implemented interfaces
- [x] `invokespecial` with ACC_SUPER: if the resolved method's class is a superclass of the current class, walk up one level from current class (not from the receiver)
- [x] Inherited methods resolve constant-pool entries against their declaring class, not the subclass they were found through

### Field resolution
- [x] Instance fields include inherited fields; declaring class determines storage slot
- [x] Static fields: one copy per class, not per object
- [x] Field shadowing: a subclass field with the same name as a superclass field hides it; `getfield` uses the reference type from the CP entry, not the runtime type

### IsAssignableTo
- [x] Class → class: walk superclass chain
- [x] Class → interface: walk superclass chain checking all interfaces recursively
- [x] Array → `java.lang.Object`: always true
- [x] Array → `java.lang.Cloneable` / `java.io.Serializable`: true
- [x] `T[]` → `S[]`: true if T is assignable to S (covariance)
- [x] Primitive array types are not assignable to each other or to Object arrays

---

## 5. java.lang

### Object
- [x] `equals(Object)` — default: reference equality
- [x] `hashCode()` — default: identity-based (any stable int is fine)
- [x] `toString()` — default: `ClassName@hexHashCode`
- [x] `getClass()` — returns a Class object (can be a stub with just getName())
- [x] `clone()` — shallow field copy; throws CloneNotSupportedException if class doesn't implement Cloneable
- [x] `notify()`, `notifyAll()`, `wait()`, `wait(long)` — no-ops or minimal stubs; applets rarely block on these correctly anyway

### String
- [x] Internal representation: char array or host string — either works
- [x] `length()`
- [x] `charAt(int)` — StringIndexOutOfBoundsException if out of range
- [x] `substring(int)`, `substring(int, int)` — end index is exclusive; StringIndexOutOfBoundsException on bad range
- [x] `indexOf(int)`, `indexOf(int, int)`, `indexOf(String)`, `indexOf(String, int)`
- [x] `lastIndexOf(int)`, `lastIndexOf(String)`
- [x] `equals(Object)` — value equality, case-sensitive
- [x] `equalsIgnoreCase(String)`
- [x] `compareTo(String)`, `compareToIgnoreCase(String)`
- [x] `startsWith(String)`, `startsWith(String, int)`, `endsWith(String)`
- [x] `contains(CharSequence)` (added 1.5 but often called — stub returning indexOf >= 0 is fine)
- [x] `replace(char, char)`
- [x] `toLowerCase()`, `toUpperCase()` — locale-insensitive versions are fine
- [x] `trim()` — strips ASCII whitespace (≤ 0x20), not Unicode whitespace
- [x] `toCharArray()`
- [x] `valueOf(boolean)`, `valueOf(char)`, `valueOf(int)`, `valueOf(long)`, `valueOf(float)`, `valueOf(double)`, `valueOf(Object)`
- [x] `concat(String)` — returns new String
- [x] `intern()` — returning the same string is acceptable; equality must still work
- [x] `toString()` — returns itself
- [x] String is immutable; operations always return new instances
- [x] `+` operator on strings compiles to StringBuffer.append chain — ensure that chain works

### StringBuffer
- [x] `append(String)`, `append(int)`, `append(long)`, `append(float)`, `append(double)`, `append(boolean)`, `append(char)`, `append(char[])`, `append(Object)` (calls toString)
- [x] `toString()` — returns String
- [x] `length()`
- [x] `charAt(int)`
- [x] `setCharAt(int, char)`
- [x] `insert(int, String)`, `insert(int, int)`, `insert(int, char)`
- [x] `delete(int, int)`, `deleteCharAt(int)`
- [x] `reverse()`
- [x] `capacity()`, `ensureCapacity(int)` — stubs are fine
- [x] String concatenation via `+` compiles to: `new StringBuffer().append(a).append(b).toString()` — this chain must work end-to-end

### Math
- [x] `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`
- [x] `sqrt`, `pow`, `exp`, `log`
- [x] `abs(int)`, `abs(long)`, `abs(float)`, `abs(double)`
- [x] `min`/`max` for all four numeric types
- [x] `floor`, `ceil`, `round(float)` (returns int), `round(double)` (returns long)
- [x] `random()` — returns double in [0.0, 1.0)
- [x] `PI`, `E` as static final double fields
- [x] `abs(Integer.MIN_VALUE)` returns `Integer.MIN_VALUE` (overflow, same as Java)

### System
- [x] `currentTimeMillis()` — milliseconds since epoch
- [x] `arraycopy(src, srcPos, dst, dstPos, length)` — NullPointerException on null; ArrayIndexOutOfBoundsException on bad range; ArrayStoreException if element types incompatible; must handle overlapping src/dst in same array correctly
- [x] `System.out.println(...)` — at minimum route to a debug log; applets use this constantly
- [x] `System.err.println(...)` — same
- [x] `System.exit(int)` — stub is fine; should not actually kill the host process
- [x] `getProperty(String)` — return null or empty for unknown keys; some applets probe `os.name`, `java.version`

### Number wrappers (Integer, Long, Float, Double, Boolean, Character)
- [x] Constructors: `new Integer(int)`, `new Integer(String)` (parses decimal)
- [x] `intValue()`, `longValue()`, `floatValue()`, `doubleValue()`
- [x] `Integer.parseInt(String)`, `Integer.parseInt(String, int radix)` — NumberFormatException on invalid input
- [x] `Long.parseLong(String)`
- [x] `Float.parseFloat(String)`, `Double.parseDouble(String)`
- [x] `Integer.toString(int)`, `Integer.toString(int, int radix)`, `Integer.toBinaryString`, `Integer.toHexString`, `Integer.toOctalString`
- [x] `Integer.MAX_VALUE`, `Integer.MIN_VALUE`, `Long.MAX_VALUE`, `Long.MIN_VALUE`
- [x] `Float.NaN`, `Float.POSITIVE_INFINITY`, `Float.NEGATIVE_INFINITY`, `Float.MAX_VALUE`, `Float.MIN_VALUE`
- [x] `Float.isNaN(float)`, `Float.isInfinite(float)`, `Double.isNaN(double)`, `Double.isInfinite(double)`
- [x] `Float.floatToIntBits(float)` / `Float.intBitsToFloat(int)` — must round-trip NaN correctly
- [x] `Double.doubleToLongBits(double)` / `Double.longBitsToDouble(long)`
- [x] `Character.isDigit`, `isLetter`, `isLetterOrDigit`, `isWhitespace`, `isUpperCase`, `isLowerCase`, `toUpperCase`, `toLowerCase`
- [x] `Boolean.TRUE`, `Boolean.FALSE`, `booleanValue()`

### Thread (minimal — just enough for animation)
- [x] `new Thread(Runnable)`, `new Thread()` (subclass override of run())
- [x] `start()` — actually runs on a real thread or queues for cooperative execution
- [x] `run()` — called by the threading system
- [x] `sleep(long ms)` — must actually sleep or yield; applets use this for frame timing
- [x] `interrupt()`, `isInterrupted()`, `interrupted()` — stubs acceptable
- [x] `isAlive()` — return true between start() and run() completion
- [x] `join()`, `join(long)` — stubs or minimal implementation
- [x] Thread safety of VM state: if you run real threads, field access must be synchronized

### Exceptions & errors (hierarchy must be correct for catch clauses to work)
- [x] `Throwable` → `Exception` → `RuntimeException`
- [x] `RuntimeException` subclasses: NullPointerException, ArrayIndexOutOfBoundsException, IndexOutOfBoundsException, StringIndexOutOfBoundsException, ClassCastException, ArithmeticException, IllegalArgumentException, IllegalStateException, NumberFormatException, ArrayStoreException, NegativeArraySizeException, UnsupportedOperationException
- [x] `Error` subclasses: StackOverflowError, OutOfMemoryError, NoClassDefFoundError, NoSuchMethodError, NoSuchFieldError, ClassFormatError
- [x] `IOException` and subclasses (at least stub them so catch clauses compile)
- [x] `getMessage()` on Throwable must return the message passed to the constructor
- [x] `printStackTrace()` — route to debug log; applets call this in catch blocks

---

## 6. java.util

### Vector
- [x] `addElement(Object)`, `add(Object)`
- [x] `elementAt(int)` — ArrayIndexOutOfBoundsException on bad index
- [x] `get(int)` — same
- [x] `size()`
- [x] `isEmpty()`
- [x] `removeElement(Object)` — removes first occurrence by equals()
- [x] `removeElementAt(int)`
- [x] `removeAllElements()` / `clear()`
- [x] `contains(Object)` — uses equals()
- [x] `indexOf(Object)`, `indexOf(Object, int)`
- [x] `insertElementAt(Object, int)`
- [x] `setElementAt(Object, int)`
- [x] `elements()` — returns Enumeration over a snapshot or live view
- [x] `copyInto(Object[])`, `toArray()` (1.1+)
- [x] Grows automatically; initial capacity 10, doubles on overflow

### Hashtable
- [x] `put(Object key, Object value)` — key and value must not be null (throws NullPointerException if null)
- [x] `get(Object key)` — returns null if not found
- [x] `remove(Object key)`
- [x] `containsKey(Object)`, `contains(Object value)` / `containsValue(Object)`
- [x] `size()`, `isEmpty()`
- [x] `keys()` — returns Enumeration of keys
- [x] `elements()` — returns Enumeration of values
- [x] Uses `equals()` and `hashCode()` for key lookup (not identity)

### Enumeration
- [x] `hasMoreElements()` — returns true if more elements remain
- [x] `nextElement()` — throws NoSuchElementException if exhausted
- [x] Enumerations from Vector/Hashtable do not need to be fail-fast

### Random
- [x] `Random()`, `Random(long seed)`
- [x] `nextInt()`, `nextInt(int bound)` — bound must be > 0; IllegalArgumentException otherwise
- [x] `nextLong()`, `nextFloat()`, `nextDouble()`, `nextBoolean()`
- [x] Two Random instances with the same seed must produce the same sequence

### Date
- [x] `Date()` — current time
- [x] `Date(long time)` — milliseconds since epoch
- [x] `getTime()` — returns long
- [x] `before(Date)`, `after(Date)`, `equals(Date)`
- [x] Deprecated methods like `getYear()`, `getMonth()`, `getDay()` — stub them; applets use them

### Stack (extends Vector)
- [x] `push(Object)` — adds to top
- [x] `pop()` — removes and returns top; EmptyStackException if empty
- [x] `peek()` — returns top without removing; EmptyStackException if empty
- [x] `empty()` — true if no elements
- [x] `search(Object)` — 1-based distance from top, -1 if not found

---

## 7. java.applet

### Applet lifecycle
- [x] `init()` — called once after construction; set up state here
- [x] `start()` — called after init and after returning from background; start animation threads here
- [x] `stop()` — called when page is hidden or navigated away; stop threads here
- [x] `destroy()` — called when applet is fully unloaded; `stop()` always called first
- [x] Lifecycle order: constructor → init → start → [stop → start]* → stop → destroy
- [x] Exceptions in lifecycle methods should be caught and logged; must not crash the browser

### Applet API
- [x] `getParameter(String name)` — reads `<param name=... value=...>` from HTML; null if not present; name matching is case-insensitive
- [x] `getParameterInfo()` — stub returning null or empty array is fine
- [x] `getCodeBase()` — URL of the directory containing the .class file
- [x] `getDocumentBase()` — URL of the HTML page
- [x] `getImage(URL)`, `getImage(URL, String name)` — returns Image (may load asynchronously)
- [x] `getAudioClip(URL)`, `getAudioClip(URL, String)` — stub returning no-op AudioClip is acceptable
- [x] `play(URL)`, `play(URL, String)` — can be a no-op
- [x] `showStatus(String)` — display in browser status bar (or debug log)
- [x] `resize(int w, int h)`, `resize(Dimension)` — update applet display area
- [x] `isActive()` — true between start() and stop()
- [x] `getAppletContext()` — returns AppletContext stub
- [x] `getAppletInfo()` — returns null by default

### AppletContext
- [x] `showDocument(URL)` — navigate browser to URL
- [x] `showDocument(URL, String target)` — `_blank`, `_self`, `_parent`, `_top`, or frame name
- [x] `showStatus(String)` — status bar message
- [x] `getApplet(String name)` — stub returning null is fine
- [x] `getApplets()` — stub returning empty Enumeration

### AudioClip
- [x] `play()`, `loop()`, `stop()` — stubs acceptable; silence is better than crashing

---

## 8. java.awt — Graphics

This is the largest single API surface. Most applets exercise it heavily.

### Core drawing
- [x] `drawLine(x1, y1, x2, y2)`
- [x] `drawRect(x, y, w, h)` — outline only
- [x] `fillRect(x, y, w, h)` — filled
- [x] `clearRect(x, y, w, h)` — fills with background color
- [x] `drawRoundRect(x, y, w, h, arcW, arcH)`
- [x] `fillRoundRect(x, y, w, h, arcW, arcH)`
- [x] `drawOval(x, y, w, h)` — inscribed in the bounding box
- [x] `fillOval(x, y, w, h)`
- [x] `drawArc(x, y, w, h, startAngle, arcAngle)` — angles in degrees, counterclockwise from 3 o'clock
- [x] `fillArc(x, y, w, h, startAngle, arcAngle)`
- [x] `drawPolygon(int[] xpoints, int[] ypoints, int npoints)`, `drawPolygon(Polygon)`
- [x] `fillPolygon(int[] xpoints, int[] ypoints, int npoints)`, `fillPolygon(Polygon)`
- [x] `drawPolyline(int[] xpoints, int[] ypoints, int npoints)` — open path, not closed
- [x] `draw3DRect`, `fill3DRect` — raised/lowered effect; stubs drawing flat rect are acceptable

### Text
- [x] `drawString(String, x, y)` — y is the baseline, not the top
- [x] `drawChars(char[], offset, count, x, y)`
- [x] `drawBytes(byte[], offset, count, x, y)` — treats bytes as char values

### Images
- [x] `drawImage(Image, x, y, ImageObserver)` — returns false if image not yet loaded
- [x] `drawImage(Image, x, y, w, h, ImageObserver)` — scaled
- [x] `drawImage(Image, x, y, bgcolor, ImageObserver)` — fill transparent areas with bgcolor
- [x] `drawImage(Image, x, y, w, h, bgcolor, ImageObserver)`
- [x] `drawImage(Image, dx1, dy1, dx2, dy2, sx1, sy1, sx2, sy2, ImageObserver)` (1.1 addition) — subimage copy with optional flip
- [x] ImageObserver.imageUpdate() called with ALLBITS flag when image finishes loading

### Color and font state
- [x] `setColor(Color)`, `getColor()`
- [x] `setFont(Font)`, `getFont()`
- [x] `getFontMetrics()`, `getFontMetrics(Font)` — must return real metrics if possible
- [x] `setPaintMode()` — normal drawing (default)
- [x] `setXORMode(Color)` — XOR drawing mode; each pixel = current ^ xorColor ^ newColor; many applets use this for animation without full redraws

### Transform & clip
- [x] `translate(int dx, int dy)` — shifts origin; cumulative
- [x] `clipRect(x, y, w, h)` — intersects with existing clip
- [x] `setClip(int x, int y, int w, int h)`, `setClip(Shape)` — replaces clip
- [x] `getClip()` — returns current clip as Shape (can return null if no clip)
- [x] `getClipBounds()` — returns bounding Rectangle of clip
- [x] `copyArea(x, y, w, h, dx, dy)` — copies a screen region; used for scrolling

### Lifecycle
- [x] `dispose()` — release resources; subsequent calls on disposed Graphics should fail gracefully
- [x] `create()` — returns a copy with the same state that can be independently disposed

---

## 9. java.awt — Color

- [x] `Color(int r, int g, int b)` — clamps or wraps values 0..255
- [x] `Color(int rgb)` — packed 0xRRGGBB
- [x] `getRed()`, `getGreen()`, `getBlue()` — returns 0..255
- [x] `getRGB()` — returns packed int with alpha=0xFF in high byte
- [x] `brighter()` — each channel scaled toward 255
- [x] `darker()` — each channel scaled toward 0; fully black stays black
- [x] `equals(Object)` — value equality by RGB
- [x] Static constants: `black`, `blue`, `cyan`, `darkGray`, `gray`, `green`, `lightGray`, `magenta`, `orange`, `pink`, `red`, `white`, `yellow`
- [x] Also `BLACK`, `BLUE`, etc. (uppercase aliases added in 1.4 — most 1.1 code uses lowercase)

---

## 10. java.awt — Font & FontMetrics

### Font
- [x] `Font(String name, int style, int size)`
- [x] Logical font names: `"Dialog"`, `"DialogInput"`, `"Monospaced"`, `"Serif"`, `"SansSerif"` — map to real host fonts
- [x] Legacy names that still appear in 1.1 code: `"Helvetica"`, `"TimesRoman"`, `"Courier"`, `"Symbol"`, `"ZapfDingbats"` — map to nearest equivalent
- [x] Style constants: `Font.PLAIN` (0), `Font.BOLD` (1), `Font.ITALIC` (2), `Font.BOLD | Font.ITALIC` (3)
- [x] `getName()`, `getStyle()`, `getSize()`
- [x] `isBold()`, `isItalic()`, `isPlain()`
- [x] `equals(Object)` — value equality by name, style, size

### FontMetrics
- [x] `getHeight()` — leading + ascent + descent
- [x] `getAscent()` — distance from baseline to top of tallest glyph
- [x] `getDescent()` — distance from baseline to bottom of deepest glyph
- [x] `getLeading()` — extra space between lines
- [x] `stringWidth(String)` — pixel width of string in this font
- [x] `charWidth(char)`, `charWidth(int)`
- [x] `charsWidth(char[], offset, count)`
- [x] `bytesWidth(byte[], offset, count)`
- [x] `getWidths()` — array of widths for first 256 chars

---

## 11. java.awt — Image & ImageObserver

### Image
- [x] `getWidth(ImageObserver)` — -1 if not yet known
- [x] `getHeight(ImageObserver)` — -1 if not yet known
- [x] `getSource()` — ImageProducer; can be a minimal stub
- [x] `flush()` — discard cached image data
- [x] Offscreen images: `Component.createImage(int w, int h)` returns a writable Image backed by a real bitmap
- [x] `getGraphics()` on an offscreen image returns a Graphics for drawing into it — double-buffering depends on this

### ImageObserver
- [x] `imageUpdate(Image, int flags, int x, int y, int w, int h)` — flags: WIDTH, HEIGHT, PROPERTIES, SOMEBITS, FRAMEBITS, ALLBITS, ERROR, ABORT
- [x] ALLBITS flag set when image is fully loaded
- [x] ERROR flag set on load failure
- [x] Return false from imageUpdate to stop receiving callbacks

### MediaTracker
- [x] `addImage(Image, int id)`
- [x] `waitForAll()` — blocks (or yields in single-threaded host) until all images done
- [x] `waitForID(int id)` — blocks until that group done
- [x] `checkAll()`, `checkAll(boolean load)` — returns true if all images ready
- [x] `checkID(int id)`
- [x] `isErrorAny()`, `isErrorID(int id)`
- [x] `statusAll(boolean load)`, `statusID(int id, boolean load)` — bitmask of LOADING/COMPLETE/ERRORED/ABORTED

---

## 12. java.awt — Component & Event Model

### Component (base for Canvas, Panel, Applet)
- [x] `paint(Graphics g)` — called when component needs drawing; default does nothing
- [x] `update(Graphics g)` — called by repaint(); default fills background then calls paint(); many applets override to skip the fill (avoids flicker)
- [x] `repaint()`, `repaint(long ms)`, `repaint(int x, int y, int w, int h)` — schedule paint; must not call paint() synchronously from applet thread
- [x] `getSize()` / `size()` — returns Dimension of the component
- [x] `getBounds()` / `bounds()` — returns Rectangle with x, y, w, h
- [x] `getLocation()` / `location()` — returns Point
- [x] `getWidth()`, `getHeight()` (1.1)
- [x] `setBackground(Color)`, `getBackground()`
- [x] `setForeground(Color)`, `getForeground()`
- [x] `setFont(Font)`, `getFont()`
- [x] `isVisible()`, `setVisible(boolean)` / `show()`, `hide()`
- [x] `isEnabled()`, `setEnabled(boolean)` / `enable()`, `disable()`
- [x] `createImage(int w, int h)` — offscreen buffer for double-buffering
- [x] `getToolkit()` — returns Toolkit; used by some applets to get screen size or images
- [x] `getParent()` — null for root applet

### 1.1 event model (listener-based)
- [x] `addMouseListener(MouseListener)`, `removeMouseListener`
- [x] `addMouseMotionListener(MouseMotionListener)`, `removeMouseMotionListener`
- [x] `addKeyListener(KeyListener)`, `removeKeyListener`
- [x] `addFocusListener(FocusListener)`, `removeFocusListener`
- [x] MouseListener: `mouseClicked`, `mousePressed`, `mouseReleased`, `mouseEntered`, `mouseExited`
- [x] MouseMotionListener: `mouseDragged`, `mouseMoved`
- [x] KeyListener: `keyPressed`, `keyReleased`, `keyTyped`
- [x] FocusListener: `focusGained`, `focusLost`

### MouseEvent
- [x] `getX()`, `getY()` — relative to component origin
- [x] `getButton()` — BUTTON1, BUTTON2, BUTTON3 (1.1 uses getModifiers() instead)
- [x] `getModifiers()` — bitmask: BUTTON1_MASK, BUTTON2_MASK, BUTTON3_MASK, SHIFT_MASK, CTRL_MASK, META_MASK, ALT_MASK
- [x] `getClickCount()` — 1 for single click, 2 for double click
- [x] `isShiftDown()`, `isControlDown()`, `isMetaDown()`, `isAltDown()`

### KeyEvent
- [x] `getKeyCode()` — VK_* constant
- [x] `getKeyChar()` — char; KEY_UNDEFINED if no char for this key
- [x] `isActionKey()` — true for function keys, arrows, etc.
- [x] VK_ constants at minimum: VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN, VK_ENTER, VK_SPACE, VK_ESCAPE, VK_TAB, VK_BACK_SPACE, VK_DELETE, VK_HOME, VK_END, VK_PAGE_UP, VK_PAGE_DOWN, VK_F1..VK_F12, VK_A..VK_Z, VK_0..VK_9
- [x] `getModifiers()` — same bitmask as MouseEvent

### 1.0 event model (many applets still use this)
- [x] `handleEvent(Event evt)` dispatched from host to applet
- [x] `mouseDown(Event, x, y)`, `mouseUp(Event, x, y)`, `mouseDrag(Event, x, y)`, `mouseMove(Event, x, y)`, `mouseEnter(Event, x, y)`, `mouseExit(Event, x, y)`
- [x] `keyDown(Event, int key)`, `keyUp(Event, int key)`
- [x] `action(Event, Object what)` — for button clicks, menu selections
- [x] `Event.id` — MOUSE_DOWN, MOUSE_UP, MOUSE_DRAG, MOUSE_MOVE, KEY_PRESS, KEY_RELEASE, ACTION_EVENT, etc.
- [x] `Event.x`, `Event.y`, `Event.key`, `Event.modifiers`, `Event.target`, `Event.arg`
- [x] `Event.SHIFT_MASK`, `Event.CTRL_MASK`, `Event.META_MASK`, `Event.ALT_MASK`

---

## 13. java.awt — Support Types

### Dimension
- [x] `Dimension(int width, int height)`
- [x] `width`, `height` fields (public)
- [x] `getSize()`, `setSize(int, int)`, `setSize(Dimension)`

### Point
- [x] `Point(int x, int y)`
- [x] `x`, `y` fields (public)
- [x] `translate(int dx, int dy)`, `equals(Object)`, `toString()`

### Rectangle
- [x] `Rectangle(int x, int y, int w, int h)`, `Rectangle(int w, int h)`, `Rectangle(Point, Dimension)`, `Rectangle()`
- [x] `x`, `y`, `width`, `height` fields (public)
- [x] `contains(int x, int y)` / `inside(int x, int y)` — 1.0 alias
- [x] `contains(Point)`, `contains(Rectangle)`
- [x] `intersects(Rectangle)`
- [x] `intersection(Rectangle)`, `union(Rectangle)`
- [x] `isEmpty()` — true if width or height ≤ 0
- [x] `translate(int dx, int dy)`
- [x] `grow(int h, int v)` — expands by h on each side horizontally, v vertically
- [x] `setBounds(int x, int y, int w, int h)`

### Insets
- [x] `Insets(int top, int left, int bottom, int right)`
- [x] `top`, `left`, `bottom`, `right` fields (public)

### Polygon
- [x] `Polygon()`, `Polygon(int[] xpoints, int[] ypoints, int npoints)`
- [x] `xpoints`, `ypoints`, `npoints` fields (public)
- [x] `addPoint(int x, int y)` — grows xpoints/ypoints arrays
- [x] `contains(int x, int y)` / `inside(int x, int y)`
- [x] `getBoundingBox()` / `getBounds()` — returns Rectangle

### Toolkit
- [x] `getScreenSize()` — returns Dimension of the screen (or the applet viewport)
- [x] `getImage(String filename)`, `getImage(URL)` — load image from path or URL
- [x] `createImage(ImageProducer)` — needed by some image-processing applets
- [x] `getFontMetrics(Font)` — returns FontMetrics for the given font
- [x] `sync()` — flush display; stub is fine

---

## 14. Applet Host Integration

### HTML parsing
- [x] `<applet code="MyApplet.class" width="200" height="150">`
- [x] `<applet code="MyApplet" ...>` — .class extension is optional
- [x] `codebase` attribute — base URL for class loading; defaults to document directory
- [x] `archive` attribute — JAR file containing classes (JAR loading is a significant feature; stub with "not supported" is acceptable initially)
- [x] `<param name="key" value="val">` — collected and exposed via getParameter()
- [x] `name` attribute — applet name for getApplet() cross-applet communication
- [x] `alt` attribute — shown if applet can't run
- [x] `<object>` and `<embed>` as alternative applet tags (lower priority)

### Class loading
- [x] Fetch .class files via HTTP from codebase URL
- [x] Cache loaded classes; don't re-fetch on every page visit
- [x] Handle inner classes: `Outer$Inner.class` naming convention
- [x] Anonymous classes: `Outer$1.class`, `Outer$2.class`
- [x] If a class references another class that hasn't been loaded, load it on demand
- [x] Class not found → NoClassDefFoundError shown to user, not a host crash

### Rendering integration
- [x] Applet gets a fixed-size drawing surface matching width/height from `<applet>` tag
- [x] Graphics origin (0,0) is top-left of the applet's area
- [x] Clip is set to the applet's bounds by default
- [x] Host's repaint loop triggers applet's update() → paint() at some sane rate (ideally 60fps cap)
- [x] Applet-initiated repaint() must not call paint() synchronously (deadlock risk)
- [x] Offscreen images created by the applet must be composited into the page correctly

### Threading
- [x] Applet lifecycle methods (init, start, stop, destroy) called from a controlled thread, not the render thread
- [x] If applet starts its own threads for animation, those threads must be able to call repaint() safely
- [x] stop() must signal animation threads to exit; threads left running after stop() are a leak
- [x] Joining animation threads in stop() with a timeout is good practice; without it, slow threads can hang navigation

### Security
- [x] No file system access
- [x] Network access restricted to the applet's origin host (or block all — simpler)
- [x] No `System.exit()` (don't let the applet kill the browser)
- [x] No reflection-based class loading bypass
- [x] Applet exceptions must be caught at the host boundary

---

## 15. Edge Cases That Bite

These don't fit neatly above but break real applets:

- [x] `String + int` at the bytecode level is `new StringBuffer().append(str).append(int).toString()` — the StringBuffer append chain must handle all primitive types
- [x] `null + "foo"` produces `"nullfoo"` (StringBuffer.append(Object) calls toString(), which returns "null" for null)
- [x] `(int)(float)` truncation: `(int) Float.MAX_VALUE` is `Integer.MAX_VALUE`, not a garbage value
- [x] Static initializers that throw: class must be marked as failed and all subsequent uses throw NoClassDefFoundError (not retry)
- [x] `<clinit>` recursion: class A's static init references class A — must not deadlock; mark initialized before running
- [x] `invokespecial` ACC_SUPER flag: without this flag (pre-JDK 1.1 classes), superclass method dispatch differs — check the flag
- [x] `instanceof null` always returns false, never throws
- [x] `checkcast null` always passes, never throws
- [x] Catching `Error` subclasses: `catch (Throwable t)` must catch JVM errors too if they're thrown as JVM exceptions
- [x] `finally` blocks compiled as `jsr`/`ret` in JDK 1.1 — both instructions must work correctly; a missing `ret` implementation breaks all finally blocks
- [x] Array covariance at runtime: `String[]` is assignable to `Object[]` but storing a non-String into it must throw ArrayStoreException
- [x] `hashCode()` used as Hashtable key: your default must be stable for the object's lifetime (can't change on GC move — not an issue in managed runtimes, but worth noting)
- [x] `equals()` / `hashCode()` contract: if you override equals in a builtin class stub, override hashCode consistently or Hashtable breaks
- [x] Signed byte in `baload`: `array[i]` where array is `byte[]` must return a sign-extended int, so element value -1 returns int -1, not 255
- [x] `caload` vs `baload`: char arrays are unsigned; byte arrays are signed — don't swap them
- [x] Long/double fields in objects: two locals wide but only one field slot — your field storage model must handle this
- [x] Thread.sleep() accuracy: applets often use sleep(1000/fps) for frame rate — if sleep rounds up aggressively, animations run slow
- [x] `repaint()` coalescing: multiple rapid repaint() calls should collapse to one paint() call, not queue unboundedly
- [x] Image loading race: applet calls `getImage()` then immediately draws it — must handle "not loaded yet" gracefully, returning false from drawImage and calling imageUpdate later
- [x] `Math.random()` seeded from time: two applets started the same millisecond should not get identical sequences — use a better seed source
- [x] `Float.floatToIntBits(Float.NaN)` must return `0x7fc00000`, not an arbitrary NaN bit pattern — canonical NaN
- [x] Negative array index in `lookupswitch`: valid Java; the match values in the table can be negative — don't treat them as unsigned
- [x] `tableswitch` with a single entry (low == high): degenerate case, must still work
- [x] `multianewarray` with dims < array rank: `new int[3][4][]` uses dims=2; the third dimension is not allocated — elements should be null
