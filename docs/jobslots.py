"""Scan a BinaryFormatter save for ArraySinglePrimitive records and report their lengths.

Record 15 (ArraySinglePrimitive) is:
    0x0F | ObjectId int32 | Length int32 | PrimitiveTypeEnum byte | payload

We only care about Boolean(1) and Int32(8) arrays, which is what the job tables are:
JobFilledAvailable[lm] is int[slots], JobCustomMaxEnabledFlag[lm] is bool[slots].
The question is whether those come out 38 (the old hardcoded count) or 39
(JobCategory.NumCategories on the current build).
"""
import struct, sys, collections

PRIM = {1: 'bool', 2: 'byte', 6: 'double', 7: 'int16', 8: 'int32', 9: 'int64',
        11: 'single', 14: 'uint16', 15: 'uint32', 16: 'uint64'}
WIDTH = {1: 1, 2: 1, 6: 8, 7: 2, 8: 4, 9: 8, 11: 4, 14: 2, 15: 4, 16: 8}

data = open(sys.argv[1], 'rb').read()
counts = collections.Counter()
i, n = 0, len(data)
while i + 10 <= n:
    if data[i] == 0x0F:
        obj_id, length = struct.unpack_from('<ii', data, i + 1)
        ptype = data[i + 9]
        if ptype in PRIM and 0 < length < 1 << 20 and obj_id > 0:
            payload_end = i + 10 + length * WIDTH[ptype]
            if payload_end <= n:
                counts[(PRIM[ptype], length)] += 1
                i = payload_end
                continue
    i += 1

print(f'{sys.argv[1]}  ({n} bytes)')
for (kind, length), c in sorted(counts.items(), key=lambda kv: -kv[1]):
    if length in (37, 38, 39, 40) or c > 3:
        print(f'  {c:5d} x {kind}[{length}]')
