# Contributing to MP Core

Thank you for considering it. MP Core is a framework, so a change here reaches every product built on it.
That is why the bar is a test and a reason, not a preference.

## Before you write code

- Read [Backend engineering conventions](docs/BACKEND-CONVENTIONS.md) before deciding layer, bounded-context,
  contract, or framework ownership.
- **A defect:** open an issue with the smallest code that shows it, what you expected, and what happened.
- **A new capability or a changed convention:** open an issue first. Every convention in MP Core is an
  architecture decision record in [docs/decisions](docs/decisions) that names where the convention comes
  from and what was weighed against it. A change to one needs the same.
- **Business logic does not belong here.** No product entity, status, workflow or rule.

## What a pull request needs

1. **A test that fails without the change.** Say in the description that you saw it fail. For a guarantee
   about failure or concurrency (a transaction, the outbox, idempotency), a test against real PostgreSQL;
   the integration tests show how.
2. A Release build with no warnings: warnings are errors in this repository.
3. All tests passing, the integration tests included. Report the real numbers.
4. Documentation in English. A convention you introduce or explain names its source: the pattern, who
   described it, and why it was chosen over the alternatives.
5. For a change to the public API of a package: say so, and say how a consumer moves.

```bash
dotnet restore MPCore.sln --locked-mode
dotnet build MPCore.sln --configuration Release --no-restore
docker compose -f eng/compose.test.yaml up -d
export MPCORE_TEST_POSTGRESQL='Host=localhost;Port=54329;Database=mpcore_it;Username=mpcore;Password=mpcore'
export MPCORE_TEST_TIMESCALE='Host=localhost;Port=54330;Database=mpcore_it;Username=mpcore;Password=mpcore'
export MPCORE_TEST_REDIS='localhost:56390'
export MPCORE_TEST_RABBITMQ='amqp://mpcore:mpcore@localhost:56720'
export MPCORE_TEST_KAFKA='localhost:59094'
dotnet test MPCore.sln --configuration Release --no-build
```

The credentials above belong to throwaway containers on your own machine and to nothing else.

`dotnet test` as written runs every test. Four of them generate a backend from the template and build it, or pack
a package: they take minutes and use the network, and carry the trait `Category=Generated` or `Category=Packaging`.
For a fast run while you work, leave them out:

```bash
dotnet test MPCore.sln --configuration Release --no-build --filter "Category!=Generated&Category!=Packaging"
```

Continuous integration runs the fast set in one job and the slow tests in a job of their own, and the release
workflow runs every test.

**The names a leak guard looks for are not in this repository.** MP Core names no product, so the guard that keeps a
product's name out of the guidance a template ships cannot list the names in its own source. A maintainer keeps them,
one per line, in `.mpcore-product-terms` at the root of the clone (git ignores it, and no package carries it), or in
the file `MPCORE_PRODUCT_TERMS_FILE` names. With the file, `eng/verify-release-artifacts.sh` and
`TemplateContractTests.No_shipped_ai_file_names_a_product` fail on a name; without it the script prints
`NOT checked` and the test is reported as skipped, with its reason, so a run that did not look says so. Both guards
are proved with a neutral marker, not a real name: self-test case 15 and
`The_product_name_guard_finds_a_term_it_is_given_in_shipped_guidance`.

On a Mac with Apple Silicon, install a native gRPC code generator once (`brew install protobuf grpc`) or
Rosetta: the one that ships with .NET's gRPC tools is built for Intel.

## What maintainers do, and nobody else

Versioning, packing and publication to nuget.org. The continuous-integration workflow builds and tests;
it never publishes. Publication is a workflow of its own, started by hand and approved by a maintainer,
with no stored key. See the `mpcore-release` skill in `.mpcore/skills`.

## Licence

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE).
