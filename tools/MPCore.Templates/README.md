# MP Core templates

Install this package from nuget.org, then use the `mpcore-backend` template directly or invoke it through
`MPCore.Cli`, which checks that the template and the packages are of one version.

```bash
dotnet new install MPCore.Templates::0.10.0
```

Generated repositories reference an exact MP Core package version.
They never receive a copied MP Core source tree.
