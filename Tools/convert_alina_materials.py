#!/usr/bin/env python3
"""Convert Alina materials to alinaFish and preserve existing texture assignments."""

import re
from pathlib import Path

ALINA_FISH_SHADER = (
    "m_Shader: {fileID: -6465566751694194690, "
    "guid: 47e84ce9e0fb4f944a982376de0e833c, type: 3}"
)
URP_LIT = "guid: 933532a4fcc9baf4fa0491de14d08ed7"
ALINA_FLORA = "guid: ec418d233a1f4e34d9bd30f4ac2c3d74"
GLTF_SHADER = "glTF-pbrMetallicRoughness"
ALINA_FISH_GUID = "47e84ce9e0fb4f944a982376de0e833c"
SKIP = {"kelp5.mat"}

SUBSTANCE_ROOT = Path("Assets/_artAssets/Alina/SubstanceTextures")

COLOR_PROPS = ["_ColorMap", "_BaseMap", "_MainTex", "baseColorTexture"]
NORMAL_PROPS = ["_Normal", "_BumpMap", "normalTexture"]
SPEC_PROPS = ["_Spec", "_MetallicGlossMap", "_SpecGlossMap", "metallicRoughnessTexture"]

PREFIX_MAP = {
    "Kelp_1_BAKED": "Kelp_1_BAKED.001",
    "Kelp_2_BAKED.001": "ALLMODELSTOSUBSTANCE_Kelp_2_BAKED.001",
    "Kelp_3_BAKED": "Kelp_3_BAKED.001",
    "Kelp_4_BAKED": "Kelp_4_BAKED.001",
    "Kelp_5_BAKED": "Kelp_5_BAKED.001",
    "Kelp_6_BAKED": "Kelp_6_BAKED.001",
    "Fern_1_BAKED": "Fern_1_BAKED.001",
    "Fern_2_BAKED": "Fern_2_BAKED.001",
    "Flower_3_1_BAKED": "Flower_3_1_BAKED.001",
    "Flower_3_2_BAKED": "Flower_3_2_BAKED.001",
}


def load_texture_guids(project_root: Path):
    guids = {}
    substance = project_root / SUBSTANCE_ROOT
    if not substance.exists():
        return guids
    for meta in substance.rglob("*.png.meta"):
        text = meta.read_text(encoding="utf-8")
        m = re.search(r"^guid: (\w+)", text, re.MULTILINE)
        if m:
            key = meta.name.replace(".png.meta", "")
            guids[key] = m.group(1)
    return guids


def substance_prefix(mat_name: str) -> str | None:
    if mat_name in PREFIX_MAP:
        return PREFIX_MAP[mat_name]
    if "_BAKED" in mat_name:
        return mat_name if mat_name.endswith(".001") else mat_name + ".001"
    return None


def find_substance(tex_guids, prefix, suffix):
    key = f"{prefix}_{suffix}"
    if key in tex_guids:
        return tex_guids[key]
    for name, guid in tex_guids.items():
        if prefix in name and suffix in name:
            return guid
    return None


def get_prop_block(content: str, prop: str) -> str | None:
    pat = rf"(    - {re.escape(prop)}:\n(?:        .*\n)+?)(?=    - |\n    m_Ints:)"
    m = re.search(pat, content)
    return m.group(1) if m else None


def block_has_texture(block: str) -> bool:
    if not block:
        return False
    flat = " ".join(block.split())
    if "m_Texture: {fileID: 0}" in flat and "guid:" not in flat:
        return False
    return "guid:" in flat or re.search(r"m_Texture: \{fileID: (?!0\b)", flat) is not None


def extract_texture_lines(block: str) -> str:
    lines = []
    capture = False
    for line in block.splitlines():
        if line.strip().startswith("m_Texture:"):
            capture = True
        if capture:
            lines.append(line)
            if line.rstrip().endswith("}"):
                break
    return "\n".join(lines)


def find_texture_lines(content: str, props: list[str]) -> str | None:
    for prop in props:
        block = get_prop_block(content, prop)
        if block and block_has_texture(block):
            return extract_texture_lines(block)
    return None


def guid_from_texture_lines(texture_lines: str | None) -> str | None:
    if not texture_lines:
        return None
    m = re.search(r"guid: (\w+)", texture_lines)
    return m.group(1) if m else None


def tex_block(prop: str, texture_lines: str | None) -> str:
    if texture_lines:
        tex = texture_lines
    else:
        tex = "        m_Texture: {fileID: 0}"
    return (
        f"    - {prop}:\n"
        f"{tex}\n"
        f"        m_Scale: {{x: 1, y: 1}}\n"
        f"        m_Offset: {{x: 0, y: 0}}\n"
    )


def upsert_prop_block(content: str, prop: str, texture_lines: str | None) -> str:
    block = tex_block(prop, texture_lines)
    pat = rf"    - {re.escape(prop)}:\n(?:        .*\n)+?(?=    - |\n    m_Ints:)"
    if re.search(pat, content):
        return re.sub(pat, block, content, count=1)
    insert_at = content.find("    m_Ints:")
    if insert_at == -1:
        insert_at = content.find("    m_Floats:")
    return content[:insert_at] + block + content[insert_at:]


def dedupe_prop_blocks(content: str, prop: str) -> str:
    pat = rf"    - {re.escape(prop)}:\n(?:        .*\n)+?(?=    - |\n    m_Ints:|\n    m_Floats:)"
    matches = list(re.finditer(pat, content))
    if len(matches) <= 1:
        return content
    for m in reversed(matches[1:]):
        content = content[: m.start()] + content[m.end() :]
    return content


def convert_mat(path: Path, tex_guids: dict) -> bool:
    if path.name in SKIP:
        return False

    content = path.read_text(encoding="utf-8")
    already_fish = ALINA_FISH_GUID in content
    needs_shader = (
        URP_LIT in content
        or ALINA_FLORA in content
        or GLTF_SHADER in content
    )

    if already_fish and not needs_shader:
        color_lines = find_texture_lines(content, ["_ColorMap"])
        if color_lines and block_has_texture(get_prop_block(content, "_ColorMap") or ""):
            return False
    elif not needs_shader and not already_fish:
        return False

    name_m = re.search(r"m_Name: (.+)", content)
    mat_name = name_m.group(1).strip() if name_m else path.stem

    color_lines = find_texture_lines(content, COLOR_PROPS)
    normal_lines = find_texture_lines(content, NORMAL_PROPS)
    spec_lines = find_texture_lines(content, SPEC_PROPS)

    prefix = substance_prefix(mat_name)
    if prefix:
        if not guid_from_texture_lines(color_lines):
            g = find_substance(tex_guids, prefix, "AlbedoTransparency")
            color_lines = f"        m_Texture: {{fileID: 2800000, guid: {g}, type: 3}}" if g else color_lines
        if not guid_from_texture_lines(normal_lines):
            g = find_substance(tex_guids, prefix, "Normal")
            normal_lines = f"        m_Texture: {{fileID: 2800000, guid: {g}, type: 3}}" if g else normal_lines
        if not guid_from_texture_lines(spec_lines):
            g = find_substance(tex_guids, prefix, "MetallicSmoothness") or find_substance(
                tex_guids, prefix, "SpecularSmoothness"
            )
            spec_lines = f"        m_Texture: {{fileID: 2800000, guid: {g}, type: 3}}" if g else spec_lines

    if needs_shader:
        content = re.sub(
            r"m_Shader: \{fileID: [^}]+\}",
            ALINA_FISH_SHADER,
            content,
            count=1,
        )

    content = upsert_prop_block(content, "_ColorMap", color_lines)
    content = upsert_prop_block(content, "_Normal", normal_lines)
    content = upsert_prop_block(content, "_Spec", spec_lines)
    content = dedupe_prop_blocks(content, "_Spec")
    content = dedupe_prop_blocks(content, "_ColorMap")
    content = dedupe_prop_blocks(content, "_Normal")

    path.write_text(content, encoding="utf-8")
    print(f"  converted: {path.name}")
    return True


def main():
    root = Path(__file__).resolve().parents[1]
    tex_guids = load_texture_guids(root)
    folders = [
        root / "Assets/_artAssets/Alina/fbx",
        root / "Assets/_artAssets/Alina/fbx_new",
    ]
    count = 0
    for folder in folders:
        if not folder.exists():
            continue
        for mat in sorted(folder.glob("*.mat")):
            if convert_mat(mat, tex_guids):
                count += 1
    print(f"Done. {count} material(s) updated.")


if __name__ == "__main__":
    main()
