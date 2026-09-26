"""Generate runtime interoperability metadata from the user's ArcGIS install.

The generated files are deliberately excluded from version control. This
script records only callable interface facts needed by the runtime and does
not copy help text, descriptions, default values, or documentation content.
"""

from __future__ import annotations

import argparse
import json
import re
from collections import defaultdict
from pathlib import Path
from typing import Any

import arcpy


WINDOWS_ABSOLUTE_PATH = re.compile(r"(?i)(?:[a-z]:[\\/]|\\\\[^\\/\s]+[\\/][^\\/\s]+)")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate local ArcGIS tool metadata for Spatial Agent Runtime."
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "local-assets",
        help="Output directory (default: repository local-assets directory).",
    )
    return parser.parse_args()


def sanitize_identifier(value: str) -> str:
    result = re.sub(r"[^0-9A-Za-z_]+", "_", value).strip("_") or "unknown_tool"
    return f"_{result}" if result[0].isdigit() else result


def split_tool_identity(tool_name: str) -> tuple[str, str]:
    if "_" not in tool_name:
        return "local", tool_name
    short_name, alias = tool_name.rsplit("_", 1)
    return alias.lower(), short_name


def parameter_name(parameter: Any, index: int) -> str:
    value = str(getattr(parameter, "name", "") or "").strip()
    return value or f"param_{index}"


def parameter_type(parameter: Any) -> str:
    datatype = str(getattr(parameter, "datatype", "") or "").lower()
    if "boolean" in datatype:
        return "boolean"
    if any(token in datatype for token in ("double", "float")):
        return "number"
    if any(token in datatype for token in ("long", "short", "integer")):
        return "integer"
    return "string"


def is_output(parameter: Any) -> bool:
    return str(getattr(parameter, "direction", "") or "").lower() == "output"


def is_required(parameter: Any) -> bool:
    return str(getattr(parameter, "parameterType", "") or "").lower() == "required"


def serialize_parameter(parameter: Any, index: int) -> dict[str, Any]:
    name = parameter_name(parameter, index)
    direction = str(getattr(parameter, "direction", "") or "Input")
    parameter_kind = str(getattr(parameter, "parameterType", "") or "Optional")
    datatype = str(getattr(parameter, "datatype", "") or "")
    return {
        "name": name,
        "type": parameter_type(parameter),
        "title": name,
        "description": f"Local interface parameter: {name}.",
        "required": is_required(parameter) and not is_output(parameter),
        "direction": direction,
        "parameter_type": parameter_kind,
        "order": index,
        "datatype": datatype,
        "multi_value": bool(getattr(parameter, "multiValue", False)),
        "is_output": is_output(parameter),
    }


def build_entry(tool_name: str) -> tuple[str, dict[str, Any]] | None:
    alias, short_name = split_tool_identity(tool_name)
    try:
        raw_parameters = list(arcpy.GetParameterInfo(tool_name))
    except Exception as error:
        print(f"skip {tool_name}: {error}")
        return None

    parameters = [
        serialize_parameter(parameter, index)
        for index, parameter in enumerate(raw_parameters)
    ]
    if not parameters:
        return None

    names = [parameter["name"] for parameter in parameters]
    inputs = [
        parameter["name"] for parameter in parameters if not parameter["is_output"]
    ]
    outputs = [
        parameter["name"] for parameter in parameters if parameter["is_output"]
    ]
    function_name = sanitize_identifier(f"{alias}_{short_name}")
    entry = {
        "name": short_name,
        "function_name": function_name,
        "display_name": short_name,
        "execution_name": f"{alias}.{short_name}",
        "summary": f"Locally discovered geoprocessing interface: {function_name}.",
        "description": f"Locally discovered geoprocessing interface: {function_name}.",
        "destructive": True,
        "risk_level": "Destructive",
        "parameter_order": names,
        "input_params": inputs,
        "output_params": outputs,
        "primary_output": outputs[0] if outputs else None,
        "params": parameters,
    }
    return alias, entry


def assert_portable(value: Any, path: str = "$root") -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            assert_portable(child, f"{path}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            assert_portable(child, f"{path}[{index}]")
    elif isinstance(value, str) and WINDOWS_ABSOLUTE_PATH.search(value):
        raise ValueError(f"absolute path captured at {path}")


def main() -> int:
    args = parse_args()
    output_directory = args.output.resolve()
    output_directory.mkdir(parents=True, exist_ok=True)

    manifest: dict[str, list[dict[str, Any]]] = defaultdict(list)
    index: dict[str, dict[str, Any]] = {}
    for tool_name in sorted(arcpy.ListTools() or []):
        built = build_entry(str(tool_name))
        if built is None:
            continue
        alias, entry = built
        manifest[f"{alias} Tools.tbx"].append(entry)
        index[entry["function_name"]] = {
            "toolbox": alias,
            "description": entry["summary"],
            "tags": [],
        }

    serializable_manifest = dict(sorted(manifest.items()))
    assert_portable(serializable_manifest)
    assert_portable(index)

    files = {
        "tools_manifest.json": serializable_manifest,
        "tools_index.json": dict(sorted(index.items())),
    }
    for file_name, payload in files.items():
        destination = output_directory / file_name
        destination.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"wrote {destination}")

    print(f"generated {len(index)} local tool interfaces")
    print("all generated tools default to Destructive until locally reviewed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
