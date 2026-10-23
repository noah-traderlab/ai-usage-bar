# -*- coding: utf-8 -*-
"""
Windows 版（C# の Json.cs / TeamCrypto.Canonical）と 1 バイト単位で一致する JSON 直列化。
署名・MAC の対象がバイト単位で一致しないと、マスター側で検証に失敗するため、ここは厳密に合わせる。
"""
import json as _json
import math


def _num(o):
    # C# の Json.Serialize：long はそのまま、double は "R"（往復可能）形式
    if isinstance(o, bool):
        return "true" if o else "false"
    if isinstance(o, int):
        return str(o)
    if isinstance(o, float):
        if math.isnan(o) or math.isinf(o):
            return "null"
        if o == int(o) and abs(o) < 1e16:
            # C# の "R" は整数値の double を "123" と出す（小数点なし）
            return str(int(o))
        r = repr(o)
        return r


def _string(s):
    out = ['"']
    for ch in s:
        c = ord(ch)
        if ch == '"':
            out.append('\\"')
        elif ch == '\\':
            out.append('\\\\')
        elif ch == '\n':
            out.append('\\n')
        elif ch == '\r':
            out.append('\\r')
        elif ch == '\t':
            out.append('\\t')
        elif c < 0x20:
            out.append('\\u%04x' % c)
        else:
            out.append(ch)
    out.append('"')
    return ''.join(out)


def dumps(o, indent=False, _level=0):
    """C# の Json.Serialize(o, indent) と同じ出力。"""
    if o is None:
        return "null"
    if isinstance(o, bool):
        return "true" if o else "false"
    if isinstance(o, int):
        return str(o)
    if isinstance(o, float):
        return _num(o)
    if isinstance(o, str):
        return _string(o)
    nl = (lambda lv: ("\n" + "  " * lv)) if indent else (lambda lv: "")
    if isinstance(o, dict):
        if not o:
            return "{}"
        parts = []
        for k, v in o.items():
            parts.append(nl(_level + 1) + _string(str(k)) + (": " if indent else ":") + dumps(v, indent, _level + 1))
        return "{" + ",".join(parts) + nl(_level) + "}"
    if isinstance(o, (list, tuple)):
        if not o:
            return "[]"
        parts = [nl(_level + 1) + dumps(v, indent, _level + 1) for v in o]
        return "[" + ",".join(parts) + nl(_level) + "]"
    return _string(str(o))


def canonical(o):
    """TeamCrypto.Canonical：辞書のキーを Ordinal（コードポイント）順に並べた JSON のバイト列。"""
    return _canon(o).encode("utf-8")


def _canon(o):
    if isinstance(o, dict):
        keys = sorted((str(k) for k in o.keys()))
        return "{" + ",".join(_string(k) + ":" + _canon(o[k]) for k in keys) + "}"
    if isinstance(o, (list, tuple)):
        return "[" + ",".join(_canon(v) for v in o) + "]"
    if o is None or isinstance(o, (bool, int, float, str)):
        return dumps(o)
    return _string(str(o))


def loads(s):
    return _json.loads(s)
