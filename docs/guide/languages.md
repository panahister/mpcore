# Languages

MP Core has one built-in language, English, and it is the default. Every product that uses MP Core decides
in its own repository which languages it serves: English and others beside it, or another language in
English's place. MP Core carries no other language, and none of a product's: its packages hold English
texts only, in neutral resource files, and no satellite assembly. `EnglishOnlyTests` holds this: it fails on
a culture-specific resource file in the source, the template or the documentation, on text in a script
other than Latin anywhere in the repository, and on a satellite assembly in the packed
`MPCore.Localization` package. [ADR-012](../decisions/ADR-012-module-layout-business-rules-and-messages.EN.md),
amendment of 2026-10-08.

What follows is the same for every language; `<culture>` stands for the culture name a product chooses,
such as `en-GB`, and `<Product>` and `<Context>` for its own names.

## How a text is found

1. **The caller's culture** is negotiated from `Accept-Language` (RFC 9110, section 12.5.4) over REST and
   from the `accept-language` metadata entry over gRPC, with the same rules: each requested culture, then
   its parents, against `SupportedCultures`; nothing matches, then `DefaultCulture`. Walking a culture's
   parents is the lookup scheme of RFC 4647, section 3.4. Both settings live in `HttpFailureOptions` and
   `GrpcFailureOptions`; both default to English (`en`).
2. **The message catalog** (`AddMPCoreMessageCatalog`) asks every source for the key in the caller's
   culture, then each parent, then its own `MessageCatalogOptions.DefaultCulture` and its parents, then the
   neutral text. Within one culture the source with the highest precedence wins:

   | Precedence | Source |
   |---|---|
   | 100 | translations edited at run time (`MPCore.Localization.EntityFrameworkCore.PostgreSql`) |
   | 0 | the product's resource files, `catalog.AddResources<T>()` |
   | −100 | MP Core's own texts, English, in the neutral file |

   This is the resource fallback .NET's `ResourceManager` uses, extended with sources that are not
   resource files.
3. **Placeholders are named**, `{limit}`, so a translation may reorder them. A key with no text in any
   culture of the chain renders nothing: the transport omits `detail`, and the catalog counts the key on
   `mpcore.localization.missing` and logs it once per key and culture.

## Adding a language

In the product's repository, never in MP Core:

1. **The product's own keys.** Beside each resource file, add its culture file:
   `Resources/<Context>Messages.<culture>.resx`. MSBuild builds it into a satellite assembly of the
   product.
2. **MP Core's own keys**, if the product wants them in that language too. Add a resource file of the
   product for them, for example `Resources/<Product>Platform.<culture>.resx` with an empty neutral
   `Resources/<Product>Platform.resx` and a marker class `<Product>Platform`, and register it:

   ```csharp
   builder.Services.AddMPCoreMessageCatalog(catalog => catalog
       .AddResources<<Context>Messages>()
       .AddResources<<Product>Platform>());
   ```

   At the product's precedence (0), its text wins over MP Core's English (−100) in that culture. A key
   it leaves out keeps MP Core's English. The keys are listed below.
3. **Serve the culture.** Add it to both transports:

   ```csharp
   builder.Services.Configure<HttpFailureOptions>(options => options.SupportedCultures.Add("<culture>"));
   builder.Services.Configure<GrpcFailureOptions>(options => options.SupportedCultures.Add("<culture>"));
   ```

4. **Texts that change while the backend runs** go into the optional table of
   `MPCore.Localization.EntityFrameworkCore.PostgreSql`, through the product's own commands; every
   instance serves a change within its refresh interval.

## Another language in English's place

A product whose callers should never see English sets its own culture as the default, with no change to
MP Core:

```csharp
builder.Services.Configure<HttpFailureOptions>(options =>
{
    options.SupportedCultures.Clear();
    options.SupportedCultures.Add("<culture>");
    options.DefaultCulture = "<culture>";
});
builder.Services.Configure<GrpcFailureOptions>(options =>
{
    options.SupportedCultures.Clear();
    options.SupportedCultures.Add("<culture>");
    options.DefaultCulture = "<culture>";
});
builder.Services.AddMPCoreMessageCatalog(
    catalog => catalog.AddResources<<Product>Platform>(),
    options => options.DefaultCulture = "<culture>");
```

Each setting has one job:

- the transports' `DefaultCulture` and `SupportedCultures` decide the culture of a request that names no
  language, or only languages the product does not serve, English included;
- the catalog's `DefaultCulture` decides where a key falls back when the caller's culture has no text for
  it: to the product's culture before MP Core's English. It matters once the product serves more than one
  language.

With the product's texts for MP Core's keys in `<culture>` (step 2 above), every MP Core message, the
validation failure and each field, a business rule, an idempotency failure, a missing sign-in, reaches the
caller in that culture over REST and over gRPC. A key the product does not translate falls back to MP Core's
English rather than to no text. `ProductDefaultCultureTests` holds all of
this, with the fixture culture `en-AU` in the product's place.

The fixture cultures of MP Core's own tests (`en-GB`, `en-AU`, `en-US-POSIX`) show these steps with English
texts marked as fixtures; they stand for any language.

## What stays English

- The problem document's `title` and the gRPC status message: fixed, safe phrases per category, identical
  on both transports ([ADR-008](../decisions/ADR-008-http-transport-and-problem-details.EN.md)). A client
  shows `detail` (gRPC `LocalizedMessage`), which follows the caller's culture, and branches on
  `errorDomain` and `errorCode`, which never change with the language.
- A field violation without any text on gRPC, which carries a fixed English description ("Invalid value.")
  rather than none.
- Log messages and exception messages, which are for operators.

## MP Core's own keys

The English text each key has in MP Core. A product translates any of them as described above; the
placeholders are the same in every language. `MPCoreMessagesCoverageTests` holds that every key MP Core
emits has a text here and that this list is complete.

| Key | English text |
|---|---|
| `mpcore.failure` | The request could not be completed. |
| `mpcore.legacy_failure` | The request could not be completed. |
| `mpcore.unexpected_failure` | An unexpected error occurred. Please try again later. |
| `mpcore.malformed_request` | The request is malformed. |
| `mpcore.request_cancelled` | The request was cancelled. |
| `mpcore.deadline_exceeded` | The request took too long and was stopped. |
| `mpcore.authentication_required` | Sign in to continue. |
| `mpcore.permission_denied` | You do not have permission to do this. |
| `mpcore.grpc_failure` | A call to another service failed. |
| `mpcore.business_rule_violation` | The request breaks a business rule. |
| `mpcore.querying.sort_field_not_allowed` | The results cannot be sorted by this field. |
| `mpcore.validation_failed` | Some values are invalid. |
| `validation.not_null` | This value is required. |
| `validation.not_empty` | This value is required. |
| `validation.null` | This value must be empty. |
| `validation.empty` | This value must be empty. |
| `validation.length` | Must be between {min_length} and {max_length} characters. |
| `validation.minimum_length` | Must be at least {min_length} characters. |
| `validation.maximum_length` | Must be at most {max_length} characters. |
| `validation.exact_length` | Must be exactly {max_length} characters. |
| `validation.less_than` | Must be less than {comparison_value}. |
| `validation.less_than_or_equal` | Must be at most {comparison_value}. |
| `validation.greater_than` | Must be greater than {comparison_value}. |
| `validation.greater_than_or_equal` | Must be at least {comparison_value}. |
| `validation.equal` | This value is not the expected one. |
| `validation.not_equal` | This value is not allowed. |
| `validation.inclusive_between` | Must be between {from} and {to}. |
| `validation.exclusive_between` | Must be between {from} and {to}, exclusive. |
| `validation.regular_expression` | This value has an invalid format. |
| `validation.email` | This is not a valid email address. |
| `validation.credit_card` | This is not a valid card number. |
| `validation.enum` | This value is not one of the allowed options. |
| `validation.string_enum` | This value is not one of the allowed options. |
| `validation.scale_precision` | Must have at most {precision} digits, {scale} of them after the decimal point. |
| `validation.predicate` | This value is invalid. |
| `validation.async_predicate` | This value is invalid. |
| `mpcore.idempotency.key_required` | This request needs an Idempotency-Key header. |
| `mpcore.idempotency.key_invalid` | The idempotency key must be 1 to 255 visible ASCII characters. |
| `mpcore.idempotency.key_reused` | This idempotency key was already used for a different request. |
