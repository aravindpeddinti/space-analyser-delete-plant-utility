"""Credential codec used by smartplantv4.ini.

This is a faithful port of encrypt()/decrypt() from Delete Plant Utility v4.5.
It MUST stay byte-compatible with the files written by Smart Engineering
Manager, so do not "improve" it.
"""
import random

ARRAYSIZE = 7  # Array size is 8 (0 to 7)
MAXCOOKIELENGTH = 59
MAXENCRYPTLENGTH = 64
ConstLetter = 65  # for letter "A"
AddTo = 3  # add to encryption string


def encrypt(in_string):
    if len(in_string) == 0:
        return ""
    if len(in_string) > MAXCOOKIELENGTH:
        return ""

    if len(in_string) <= ARRAYSIZE:
        if len(in_string) % 2 == 0:
            key = len(in_string) // 2
        else:
            key = (1 + len(in_string)) // 2
    else:
        random.seed()
        key = int(random.randint(1, ARRAYSIZE))

    code = [[0] * (ARRAYSIZE + 1) for _ in range(ARRAYSIZE + 1)]
    ecode = [0] * ((ARRAYSIZE + 1) * (ARRAYSIZE + 1))

    code[0][0] = key + ConstLetter

    i = -1
    m = 0
    j = 0
    while m < len(in_string):
        if i == -1:
            j = 1
        else:
            j = 0
        i += 1
        while j <= ARRAYSIZE and m < len(in_string):
            code[i][j] = ord(in_string[m])
            j += 1
            m += 1

    for n in range(j, ARRAYSIZE + 1):
        code[i][n] = ord(" ")
    for n in range(i + 1, ARRAYSIZE + 1):
        for l in range(ARRAYSIZE + 1):
            code[n][l] = ord(" ")

    ecode[0] = key + ConstLetter

    index = 1
    for j in range(key, ARRAYSIZE + 1):
        for i in range(ARRAYSIZE + 1):
            ecode[index] = code[i][j]
            index += 1
    for j in range(key):
        for i in range(ARRAYSIZE + 1):
            if not (i == 0 and j == 0):
                ecode[index] = code[i][j]
                index += 1

    e_string = chr(int(ecode[0]))
    for j in range(1, 64):
        e_string += chr(int(ecode[j] + AddTo))
    return e_string


def decrypt(encrypted_string):
    if len(encrypted_string) == 0:
        return ""
    if len(encrypted_string) != MAXENCRYPTLENGTH:
        return ""

    key = ord(encrypted_string[0]) - ConstLetter
    dcode = [0] * ((ARRAYSIZE + 1) * (ARRAYSIZE + 1))

    index = 1
    for j in range(key, ARRAYSIZE + 1):
        for i in range(ARRAYSIZE + 1):
            dcode[i * (ARRAYSIZE + 1) + j] = ord(encrypted_string[index]) - AddTo
            index += 1
    for j in range(key):
        for i in range(ARRAYSIZE + 1):
            if not (i == 0 and j == 0):
                dcode[i * (ARRAYSIZE + 1) + j] = ord(encrypted_string[index]) - AddTo
                index += 1

    out_string = ""
    for i in range(1, (ARRAYSIZE + 1) * ARRAYSIZE):
        out_string += chr(int(dcode[i]))
    return out_string.strip()
