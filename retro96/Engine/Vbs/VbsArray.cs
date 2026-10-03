using System;
using System.Collections.Generic;

namespace Retro96.Engine.Vbs;

/// <summary>
/// VBScript array: 0-based (LBound always 0), up to many dimensions, row-major
/// storage. Fixed arrays come from `Dim a(5)` (6 elements); dynamic arrays
/// from `Dim a()` + `ReDim`. Erase clears fixed arrays and deallocates
/// dynamic ones. ReDim Preserve may only change the LAST dimension.
/// </summary>
public sealed class VbsArray
{
    private int[] _lengths = Array.Empty<int>();
    private VbsVariant[] _elements = Array.Empty<VbsVariant>();

    public bool IsDynamic { get; private set; }
    public bool Erased { get; private set; }
    public int Rank => _lengths.Length;

    private VbsArray() { }

    /// <summary>Dynamic, unallocated (`Dim a()` — usable only after ReDim).</summary>
    public static VbsArray Unallocated()
    {
        var a = new VbsArray { IsDynamic = true, Erased = true };
        return a;
    }

    public static VbsArray Allocate(bool dynamic, params int[] lengths)
    {
        long total = 1;
        foreach (int n in lengths)
        {
            if (n < 0)
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range");
            total *= n;
            if (total > int.MaxValue)
                throw new VbsRuntimeException(VbsErrorNumbers.OutOfMemory, "Out of memory");
        }
        return new VbsArray
        {
            IsDynamic = dynamic,
            _lengths = (int[])lengths.Clone(),
            _elements = new VbsVariant[total]
        };
    }

    public int GetLength(int dim)
    {
        if (dim < 0 || dim >= Rank)
            throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                "Subscript out of range");
        return _lengths[dim];
    }

    public VbsVariant Get(int[] indices)
    {
        Validate(indices);
        return _elements[Linear(indices)];
    }

    public void Set(int[] indices, VbsVariant value)
    {
        Validate(indices);
        _elements[Linear(indices)] = value;
    }

    /// <summary>For Each order (row-major).</summary>
    public IEnumerable<VbsVariant> Elements()
    {
        if (Erased) yield break;
        foreach (var e in _elements) yield return e;
    }

    /// <summary>Erase: fixed → reset every element to Empty; dynamic → deallocate.</summary>
    public void Erase()
    {
        if (IsDynamic)
        {
            Erased = true;
            _lengths = Array.Empty<int>();
            _elements = Array.Empty<VbsVariant>();
        }
        else
        {
            Array.Clear(_elements, 0, _elements.Length);
        }
    }

    /// <summary>ReDim without Preserve — fresh contents, still dynamic.</summary>
    public VbsArray Redim(int[] newLengths) => Allocate(true, newLengths);

    /// <summary>ReDim Preserve — only the last dimension may change (error 10).</summary>
    public VbsArray RedimPreserve(int[] newLengths)
    {
        if (Erased) return Redim(newLengths);
        if (newLengths.Length != Rank)
            throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                "Subscript out of range");
        for (int d = 0; d < Rank - 1; d++)
        {
            if (newLengths[d] != _lengths[d])
                throw new VbsRuntimeException(VbsErrorNumbers.ArrayIsFixedOrLocked,
                    "This array is fixed or temporarily locked");
        }

        var result = Allocate(true, newLengths);
        // Copy row by row: each "row" is one value of the first Rank-1 dims.
        int oldLast = _lengths[^1];
        int newLast = newLengths[^1];
        int rows = 1;
        for (int d = 0; d < Rank - 1; d++) rows *= _lengths[d];
        int copy = Math.Min(oldLast, newLast);
        for (int row = 0; row < rows; row++)
        {
            int oldBase = row * oldLast;
            int newBase = row * newLast;
            for (int i = 0; i < copy; i++)
                result._elements[newBase + i] = _elements[oldBase + i];
        }
        return result;
    }

    private void Validate(int[] indices)
    {
        if (Erased)
            throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                "Subscript out of range");
        if (indices.Length != Rank)
            throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                "Subscript out of range");
        for (int dim = 0; dim < indices.Length; dim++)
        {
            int i = indices[dim];
            if (i < 0)
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range");
            if (i >= _lengths[dim])
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range");
        }
    }

    private int Linear(int[] indices)
    {
        int linear = indices[0];
        for (int d = 1; d < Rank; d++)
        {
            if (indices[d] >= _lengths[d])
                throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                    "Subscript out of range");
            linear = linear * _lengths[d] + indices[d];
        }
        if (linear >= _lengths[0])
            throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                "Subscript out of range");
        if (linear >= _elements.Length)
            throw new VbsRuntimeException(VbsErrorNumbers.SubscriptOutOfRange,
                "Subscript out of range");
        return linear;
    }
}