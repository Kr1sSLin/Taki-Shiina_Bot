# Room schema baseline

`schemas/` is the KSP output directory configured by `core/database/build.gradle.kts` and is intended to be version-controlled.

Schema export was enabled only after the database had reached version 4, so the repository has no authoritative Room-generated JSON for versions 1–3. Those historical files are intentionally not fabricated. The migration instrumentation test reconstructs the minimal v1 database from the original entity shape and then runs the production `MIGRATION_1_2`, `MIGRATION_2_3`, and `MIGRATION_3_4` chain; the final database is validated against the generated v4 schema.

Future schema changes must retain every generated JSON version in this directory and add a migration test before increasing `AppDatabase.version`.
