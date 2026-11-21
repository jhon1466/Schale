"""Minimal Android binary XML (AXML) reader/editor for AndroidManifest.xml.

Only what merging splits needs: list elements/attributes, change an attribute's typed
value in place, and drop whole elements (e.g. <meta-data android:name="com.android.vending.splits">).
"""

from __future__ import annotations

import struct
from dataclasses import dataclass

RES_XML_START_ELEMENT = 0x0102
RES_XML_END_ELEMENT = 0x0103
TYPE_STRING = 0x03
TYPE_BOOLEAN = 0x12


@dataclass
class Attr:
    offset: int  # absolute offset of the 20-byte attribute record
    ns: int
    name: str
    raw: int
    data_type: int
    data: int


@dataclass
class Element:
    offset: int  # start chunk offset
    size: int
    name: str
    attrs: list[Attr]
    end_offset: int = -1  # matching end chunk offset
    end_size: int = 0


class Manifest:
    def __init__(self, data: bytes):
        self.data = bytearray(data)
        self.strings = self._read_strings()
        self.elements = self._read_elements()

    def _read_strings(self) -> list[str]:
        pool = 8  # after the XML header
        _, header_size, _ = struct.unpack_from("<HHI", self.data, pool)
        count, _, flags, strings_start, _ = struct.unpack_from("<IIIII", self.data, pool + 8)
        utf8 = bool(flags & 0x100)
        offsets = struct.unpack_from(f"<{count}I", self.data, pool + header_size)
        base = pool + strings_start
        out = []
        for off in offsets:
            p = base + off
            if utf8:
                n = self.data[p]
                p += 2 if n & 0x80 else 1
                length = self.data[p]
                if length & 0x80:
                    length = ((length & 0x7F) << 8) | self.data[p + 1]
                    p += 1
                p += 1
                out.append(bytes(self.data[p : p + length]).decode("utf-8", "replace"))
            else:
                length = struct.unpack_from("<H", self.data, p)[0]
                out.append(bytes(self.data[p + 2 : p + 2 + length * 2]).decode("utf-16-le", "replace"))
        return out

    def _read_elements(self) -> list[Element]:
        elements, stack = [], []
        p = 8
        while p < len(self.data):
            chunk_type, header_size, size = struct.unpack_from("<HHI", self.data, p)
            if chunk_type == RES_XML_START_ELEMENT:
                name_idx = struct.unpack_from("<I", self.data, p + 20)[0]
                attr_start, attr_size, attr_count = struct.unpack_from("<HHH", self.data, p + 24)
                attrs = []
                for i in range(attr_count):
                    a = p + 16 + attr_start + i * attr_size
                    ns, name, raw, _, _, dtype, value = struct.unpack_from("<IIIHBBI", self.data, a)
                    attrs.append(Attr(a, ns, self.strings[name], raw, dtype, value))
                el = Element(p, size, self.strings[name_idx], attrs)
                elements.append(el)
                stack.append(el)
            elif chunk_type == RES_XML_END_ELEMENT:
                el = stack.pop()
                el.end_offset, el.end_size = p, size
            p += size
        return elements

    def string(self, attr: Attr) -> str | None:
        return self.strings[attr.data] if attr.data_type == TYPE_STRING else None

    def set_boolean(self, attr: Attr, value: bool) -> None:
        struct.pack_into("<I", self.data, attr.offset + 16, 0xFFFFFFFF if value else 0)

    def remove_attr(self, element: Element, attr: Attr) -> None:
        """Neutralise an attribute by renaming it to an unused-looking resource id is unsafe; instead drop it."""
        index = element.attrs.index(attr)
        p = element.offset
        attr_start, attr_size, attr_count = struct.unpack_from("<HHH", self.data, p + 24)
        start = p + 16 + attr_start + index * attr_size
        del self.data[start : start + attr_size]
        struct.pack_into("<H", self.data, p + 28, attr_count - 1)
        struct.pack_into("<I", self.data, p + 4, element.size - attr_size)
        self._resize(-attr_size)
        self.elements = self._read_elements()

    def remove_element(self, element: Element) -> None:
        """Drop a leaf element (start + end chunks)."""
        assert element.end_offset == element.offset + element.size, "only leaf elements"
        total = element.size + element.end_size
        del self.data[element.offset : element.offset + total]
        self._resize(-total)
        self.elements = self._read_elements()

    def _resize(self, delta: int) -> None:
        struct.pack_into("<I", self.data, 4, struct.unpack_from("<I", self.data, 4)[0] + delta)

    def dump(self) -> list[str]:
        lines = []
        for el in self.elements:
            parts = []
            for a in el.attrs:
                value = self.string(a) if a.data_type == TYPE_STRING else hex(a.data)
                parts.append(f"{a.name}={value}")
            lines.append(f"<{el.name} " + " ".join(parts) + ">")
        return lines
