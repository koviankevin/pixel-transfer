"""Small systematic RS(255,223) codec over GF(256), primitive polynomial 0x11d.

Transport error correction only; AES-GCM authenticates the final file.
"""
EXP = [0] * 512
LOG = [0] * 256
v = 1
for i in range(255):
    EXP[i] = v
    LOG[v] = i
    v <<= 1
    if v & 256:
        v ^= 0x11D
for i in range(255, 512):
    EXP[i] = EXP[i - 255]


def mul(a, b):
    return EXP[LOG[a] + LOG[b]] if a and b else 0


def div(a, b):
    if not b:
        raise ValueError('無法修復的糾錯區塊')
    return EXP[(LOG[a] - LOG[b]) % 255] if a else 0


def evaluate(poly, x):
    value = 0
    for coefficient in poly:
        value = mul(value, x) ^ coefficient
    return value


GENERATOR = [1]
for i in range(32):
    product = [0] * (len(GENERATOR) + 1)
    for j, coefficient in enumerate(GENERATOR):
        product[j] ^= coefficient
        product[j + 1] ^= mul(coefficient, EXP[i])
    GENERATOR = product


def encode(data):
    if len(data) != 223:
        raise ValueError('RS 輸入必須為 223 bytes')
    result = list(data) + [0] * 32
    for i in range(223):
        coefficient = result[i]
        if coefficient:
            for j in range(1, 33):
                result[i + j] ^= mul(coefficient, GENERATOR[j])
    return data + bytes(result[-32:])


def decode(data):
    if len(data) != 255:
        raise ValueError('RS 區塊必須為 255 bytes')
    syndromes = [evaluate(data, EXP[i]) for i in range(32)]
    if not any(syndromes):
        return bytes(data[:223])
    # Berlekamp-Massey; locator coefficients in ascending order.
    locator, previous = [1] + [0] * 32, [1] + [0] * 32
    length, shift, last = 0, 1, 1
    for n in range(32):
        discrepancy = syndromes[n]
        for j in range(1, length + 1):
            discrepancy ^= mul(locator[j], syndromes[n - j])
        if discrepancy == 0:
            shift += 1
            continue
        old = locator[:]
        ratio = div(discrepancy, last)
        for j in range(33 - shift):
            locator[j + shift] ^= mul(ratio, previous[j])
        if 2 * length <= n:
            length, previous, last, shift = n + 1 - length, old, discrepancy, 1
        else:
            shift += 1
    if not 1 <= length <= 16:
        raise ValueError('錯誤超過糾錯能力')
    positions = [p for p in range(255)
                 if evaluate(list(reversed(locator[:length + 1])), EXP[-(254 - p) % 255]) == 0]
    if len(positions) != length:
        raise ValueError('找不到完整錯誤位置')
    # Solve the first L syndrome equations for error magnitudes.
    matrix = [[EXP[((254 - p) * row) % 255] for p in positions] + [syndromes[row]]
              for row in range(length)]
    for col in range(length):
        pivot = next((row for row in range(col, length) if matrix[row][col]), None)
        if pivot is None:
            raise ValueError('無法修復的糾錯區塊')
        matrix[col], matrix[pivot] = matrix[pivot], matrix[col]
        scale = matrix[col][col]
        matrix[col] = [div(value, scale) for value in matrix[col]]
        for row in range(length):
            if row != col and matrix[row][col]:
                ratio = matrix[row][col]
                matrix[row] = [a ^ mul(ratio, b) for a, b in zip(matrix[row], matrix[col])]
    corrected = bytearray(data)
    for row, position in enumerate(positions):
        corrected[position] ^= matrix[row][-1]
    if any(evaluate(corrected, EXP[i]) for i in range(32)):
        raise ValueError('修復後仍有錯誤')
    return bytes(corrected[:223])
