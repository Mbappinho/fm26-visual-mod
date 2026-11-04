# SPDX-License-Identifier: MIT
"""Valve KeyValues (VDF/ACF) text parser."""


class VdfError(ValueError):
    pass


def _tokens(text):
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c.isspace():
            i += 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j + 1
        elif c in "{}":
            yield c
            i += 1
        elif c == '"':
            i += 1
            buf = []
            while True:
                if i >= n:
                    raise VdfError("unterminated string")
                if text[i] == '"':
                    break
                if text[i] == "\\":
                    i += 1
                    if i >= n:
                        raise VdfError("unterminated string")
                buf.append(text[i])
                i += 1
            yield ("s", "".join(buf))
            i += 1
        else:
            i += 1


def parse(text):
    stack = [{}]
    key = None
    for tok in _tokens(text):
        if tok == "{":
            child = {}
            stack[-1][key] = child
            stack.append(child)
            key = None
        elif tok == "}":
            stack.pop()
        elif key is None:
            key = tok[1]
        else:
            stack[-1][key] = tok[1]
            key = None
    if len(stack) != 1:
        raise VdfError("unterminated block")
    return stack[0]
