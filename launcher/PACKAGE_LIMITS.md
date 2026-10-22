# Package resource limits

The launcher validates signed metadata, expected package size and SHA-256 before
installation. Download and extraction budgets also limit resource consumption:

| Process environment override | Default |
|---|---:|
| KSR_ZIP_MAX_ARCHIVE_BYTES | 1073741824 (1 GiB compressed) |
| KSR_ZIP_MAX_TOTAL_BYTES | 4294967296 (4 GiB extracted per archive) |
| KSR_ZIP_MAX_FILE_BYTES | 536870912 (512 MiB per file) |
| KSR_ZIP_MAX_ENTRIES | 50000 |

Restart the launcher after changing overrides. Positive numeric values are
required. Normally no override is needed. Each signed component's declared
download size is also enforced, including actual bytes received when HTTP
headers are missing or misleading.

ZIP metadata is checked before output is created. Extracted bytes are counted
with an 80 KiB buffer; lengths and CRC are checked per file. Duplicate output
paths, traversal and symbolic/reparse links are rejected. Failed preparation
does not replace the installed component. Existing preservation and rollback
behavior remains in use.

No periodic task is introduced. These checks run only while a package is being
downloaded or extracted. All 15 packages in the official catalog were checked
against the defaults during the initial validation.
