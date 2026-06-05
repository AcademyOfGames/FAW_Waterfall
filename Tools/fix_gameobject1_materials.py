#!/usr/bin/env python3
"""Fix GameObject (1) prefab material overrides and convert only those materials."""

import re
from pathlib import Path

# FBX/GLB source guid -> external BAKED material guid used by that model
SOURCE_TO_MATERIAL = {
    "4ce07e9d1a1e1fa458b368d581f2b9e5": "84079f16360cfb745b56dd10ce9e5543",  # Kelp_1
    "eaca69f55cf4c004f9172abaace15c57": "4e4e81b9bf2bfa74c9d38e0934366552",  # Kelp_2
    "dd403a66fe2b1254b8e42c947ca37ed4": "9ce2bbca9ec5c464da59aea738d1557d",  # Kelp_3
    "e4a95298229ad074487cee3f163eb614": "3c25d1f7c65e5b54cbfcfe3bf9b81f13",  # Kelp_4
    "f53a5c0f6441ffd4bb70ca4dee3d9920": "fb860b1e3cc39d54394bc126ffda7ed4",  # Kelp_5
    "fd9232bb2dac47d4aa95acc0bac9e526": "ca0a1b7f5643a2a4f8f2ae59cdf46128",  # Kelp_6
    "b239f0b74e04fe94a83cb6ee5b7210fb": "4852db7b4fa326746a66a96c3bbdbe34",  # Flower_3_1
    "42f119e8dc9f3c048a4d3c5399d6a445": "e806debff4e6cc1489c986804564f418",  # Flower_3_2
    "152727a9ed2b41048b220acd706ab3f0": "4881cf616d9efd04483cff920375fdd0",  # gourd_r2.glb
}

# Wrong materials previously assigned on prefab instances
WRONG_MATERIALS = {
    "7e1527b388b2e6847bb630d886872377",  # Fish_4_Mat
    "a5db78d270c9b754cbc95608642aef03",  # kelp5 (legacy, use Kelp_5_BAKED instead)
}

GAMEOBJECT1_MATERIALS = set(SOURCE_TO_MATERIAL.values())

ALINA_FISH_SHADER = (
    "m_Shader: {fileID: -6465566751694194690, "
    "guid: 47e84ce9e0fb4f944a982376de0e833c, type: 3}"
)
ALINA_FISH_GUID = "47e84ce9e0fb4f944a982376de0e833c"

COLOR_PROPS = ["_ColorMap", "_BaseMap", "_MainTex", "baseColorTexture"]
NORMAL_PROPS = ["_Normal", "_BumpMap", "normalTexture"]
SPEC_PROPS = ["_Spec", "_MetallicGlossMap", "_SpecGlossMap", "metallicRoughnessTexture"]

SUBSTANCE_ROOT = Path("Assets/_artAssets/Alina/SubstanceTextures")
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


def find_mat_path(project_root: Path, guid: str) -> Path | None:
    for mat in (project_root / "Assets/_artAssets/Alina").rglob("*.mat.meta"):
        if f"guid: {guid}" in mat.read_text(encoding="utf-8"):
            return mat.with_suffix("")
    return None


def fix_prefab_materials(prefab_path: Path) -> int:
    content = prefab_path.read_text(encoding="utf-8")
    fixed = 0
    material_pat = re.compile(
        r"(propertyPath: m_Materials\.Array\.data\[0\]\s*\n\s*value:\s*\n\s*"
        r"objectReference: \{fileID: 2100000, guid: )(\w+)(, type: 2\})"
    )

    blocks = re.split(r"(?=--- !u!1001 &)", content)
    for i, block in enumerate(blocks):
        if not block.startswith("--- !u!1001"):
            continue
        source_m = re.search(
            r"m_SourcePrefab: \{fileID: [^,]+, guid: (\w+),\s*\n?\s*type: 3\}", block
        )
        if not source_m:
            continue
        correct_mat = SOURCE_TO_MATERIAL.get(source_m.group(1))
        if not correct_mat:
            continue

        def replacer(match, correct=correct_mat):
            nonlocal fixed
            wrong_guid = match.group(2)
            if wrong_guid in WRONG_MATERIALS or wrong_guid != correct:
                fixed += 1
                return f"{match.group(1)}{correct}{match.group(3)}"
            return match.group(0)

        blocks[i] = material_pat.sub(replacer, block)

    prefab_path.write_text("".join(blocks), encoding="utf-8")
    return fixed


def get_prop_block(content: str, prop: str) -> str | None:
    pat = rf"(    - {re.escape(prop)}:\n(?:        .*\n)+?)(?=    - |\n    m_Ints:)"
    m = re.search(pat, content)
    return m.group(1) if m else None


def block_has_texture(block: str) -> bool:
    if not block:
        return False
    flat = " ".join(block.split())
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


def tex_block(prop: str, texture_lines: str | None) -> str:
    tex = texture_lines if texture_lines else "        m_Texture: {fileID: 0}"
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


def convert_material(path: Path, tex_guids: dict) -> bool:
    content = path.read_text(encoding="utf-8")
    name_m = re.search(r"m_Name: (.+)", content)
    mat_name = name_m.group(1).strip() if name_m else path.stem

    color_lines = find_texture_lines(content, COLOR_PROPS)
    normal_lines = find_texture_lines(content, NORMAL_PROPS)
    spec_lines = find_texture_lines(content, SPEC_PROPS)

    prefix = substance_prefix(mat_name)
    if prefix:
        if not color_lines or not block_has_texture(get_prop_block(content, "_ColorMap") or ""):
            g = find_substance(tex_guids, prefix, "AlbedoTransparency")
            if g:
                color_lines = f"        m_Texture: {{fileID: 2800000, guid: {g}, type: 3}}"
        if not normal_lines:
            g = find_substance(tex_guids, prefix, "Normal")
            if g:
                normal_lines = f"        m_Texture: {{fileID: 2800000, guid: {g}, type: 3}}"
        if not spec_lines:
            g = find_substance(tex_guids, prefix, "MetallicSmoothness") or find_substance(
                tex_guids, prefix, "SpecularSmoothness"
            )
            if g:
                spec_lines = f"        m_Texture: {{fileID: 2800000, guid: {g}, type: 3}}"

    if ALINA_FISH_GUID not in content:
        content = re.sub(
            r"m_Shader: \{fileID: [^}]+\}",
            ALINA_FISH_SHADER,
            content,
            count=1,
        )

    content = upsert_prop_block(content, "_ColorMap", color_lines)
    content = upsert_prop_block(content, "_Normal", normal_lines)
    content = upsert_prop_block(content, "_Spec", spec_lines)

    path.write_text(content, encoding="utf-8")
    return True


def main():
    root = Path(__file__).resolve().parents[1]
    prefab = root / "Assets/_artAssets/Alina/Alina_MainPrefab 1.prefab"
    tex_guids = load_texture_guids(root)

    fixed = fix_prefab_materials(prefab)
    print(f"Fixed {fixed} material override(s) in prefab.")

    converted = 0
    for guid in sorted(GAMEOBJECT1_MATERIALS):
        mat_path = find_mat_path(root, guid)
        if mat_path and mat_path.exists():
            convert_material(mat_path, tex_guids)
            print(f"  converted: {mat_path.name}")
            converted += 1

    print(f"Done. {converted} GameObject (1) material(s) updated.")


if __name__ == "__main__":
    main()
