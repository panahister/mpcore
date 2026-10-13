# Contracts of the command line tool and of the template

Two files record what a consumer of MP Core depends on besides the packages' public API, so that a change to it
is a decision and not an accident:

| File | What it records |
|---|---|
| [`cli.json`](cli.json) | The `mpcore` command line tool: each command (`new backend`, `configure`), each option with whether it is required, whether it is a flag, a value or a choice, its choices and its default; the exit code of each probed situation; the presets; the manifest that `new backend` writes, with its schema version and the kind of each member |
| [`template.json`](template.json) | The template `mpcore-backend`: each parameter with its type, whether it is required, its choices and its default; the files it generates for each shape and transport, as the files common to all six and the files each adds |

Neither file is written by hand. Tests generate them by running the built tool and the template from this
checkout (`GeneratedBackendTests`), compare the result with the file, and fail when they differ. The tool is run
with a probe for each situation the file names, so an exit code in `cli.json` is one that was seen.

## The rule

A contract is read as the set of facts it states, one per value: `commands.new backend.options.shape.default=service`.
A later contract is **compatible** with an earlier one when it states every fact the earlier one stated.

- **An addition** (a new option, a new choice, a new file) widens the contract. The test of the file fails until
  the file is updated, so the update is in the same commit as the change.
- **A removal or a change** (an option gone or renamed, a choice removed, a default changed, an exit code
  changed, a file no longer generated) is a fact that is no longer stated. `ContractCompatibilityTests` compares the
  file with the file at the tag of the baseline version (`MPCoreBaselineVersion` in `src/Directory.Build.props`) and
  fails when facts are gone, unless the declared version may break the contract. Until the compatibility policy of
  1.0 replaces it, that is a new minor version while the major version is `0`, and a new major version after.

The first contract is published with 1.0.0; a baseline older than that has no contract to compare with, and the
test says so and checks nothing else.

## Updating a file

After a change that adds to the tool or the template, on a person's machine:

```bash
MPCORE_UPDATE_CONTRACTS=1 dotnet test tests/MPCore.Transport.Grpc.Tests --filter "FullyQualifiedName~The_checked_in_"
```

Read the diff before committing it: every line is a promise. The variable is refused where `CI` is set.

## What they do not record

The words of messages and of usage text (a message may be reworded), the output of `configure` beyond its exit
code, and exit codes of situations the tests cannot set up (a template of another version installed, a failed
restore). Those are covered by tests of their own or not at all, and the files do not claim otherwise.
