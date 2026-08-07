# Fake data: from one-liner to fully customized

The hosting package bundles [LdifDotNet.Generator](https://www.nuget.org/packages/LdifDotNet.Generator), which has two generators:

| Generator | Entry shape | Customization | Exposed as |
|---|---|---|---|
| `LdifGenerator` | Fixed `inetOrgPerson` / `groupOfNames` | Seed, locale, base DN, dangling-member ratio | `WithFakePeople` / `WithFakeGroups` / `WithFakeDirectory` |
| `SchemaEntryGenerator` | Any object class in a parsed LDAP schema | Per-attribute format templates, example value pools, auxiliary classes, RDN choice, optional-attribute fill | `WithSeedRecords` (bring your own generator call) |

Use the builder extensions when the standard entry shape is enough — see the [package README](../aspireOpenLdap/Aspire.Hosting.OpenLdap/README.md#seeding-with-fake-data). Use `SchemaEntryGenerator` when you need entries whose attributes match a pre-defined format (employee-number patterns, corporate mail addresses, controlled vocabularies) or extra object classes such as `eduPerson` or `posixAccount`. This guide covers that advanced path. All examples were run against `LdifDotNet.Generator` 0.6.0; the LDIF shown is real output.

## Prerequisite: schema files

`SchemaEntryGenerator` reads the same `.schema` files `slapd` uses. The packages do not ship them. Copy the ones you need into your repository from the [OpenLDAP source tree](https://git.openldap.org/openldap/openldap/-/tree/master/servers/slapd/schema) (`servers/slapd/schema/`) or from any Linux machine with OpenLDAP installed (`/etc/ldap/schema/`). For `inetOrgPerson` you need `core.schema`, `cosine.schema`, and `inetorgperson.schema`, in that load order.

## Full example

```csharp
using LdifDotNet.Generator;
using LdifDotNet.Schema;

var schema = LdapSchema.Load(
    "schemas/core.schema",
    "schemas/cosine.schema",
    "schemas/inetorgperson.schema",
    "schemas/eduperson.schema");            // only if you mix in eduPerson

var options = new SchemaGeneratorOptions
{
    Seed = 42,                              // same seed + same package version => identical entries
    OptionalAttributeFill = 0.4,            // fill 40% of MAY attributes (MUST are always filled)
    RdnAttribute = "uid",                   // default picks uid, then cn, then the first MUST
};
options.AuxiliaryClasses.Add("eduPerson");

// Format templates: Bogus handlebars tokens plus literal text
options.Formatters["uid"] = "u{{randomizer.replacenumbers(######)}}";
options.Formatters["employeeNumber"] = "EMP-{{randomizer.replacenumbers(#####)}}";
options.Formatters["mail"] = "{{name.firstName}}.{{name.lastName}}@corp.example";

// Example pools: values drawn from a fixed list
options.ExampleValues["eduPersonAffiliation"] = ["faculty", "student", "staff"];
options.ExampleValues["ou"] = ["Engineering", "Research", "Operations"];

var generator = new SchemaEntryGenerator(schema, options);
var people = generator.Entries("inetOrgPerson", 100, "ou=people,dc=example,dc=org");

var ldap = builder.AddOpenLdap("ldap")
    .WithOrganizationalUnit("people")       // declares the parent OU and the base-DN root entry
    .WithSeedRecords(people);
```

The start of the first generated entry (seed 42):

```ldif
dn: uid=u611512,ou=people,dc=example,dc=org
objectClass: top
objectClass: person
objectClass: organizationalPerson
objectClass: inetOrgPerson
objectClass: eduPerson
uid: u611512
sn: Reilly
cn: Jonas Daniel
employeeNumber: EMP-10785
mail: Kaia.Schneider@corp.example
ou: Engineering
...
```

## How a value is chosen

For each attribute the generator uses the first source that applies:

1. **`Formatters[attribute]`** — your template, rendered with Bogus tokens.
2. **`ExampleValues[attribute]`** — one value picked from your pool.
3. **Built-in name heuristics** — realistic values for well-known names (`cn`, `sn`, `mail`, `telephoneNumber`, `uidNumber`, `homeDirectory`, ~20 more), used only when the value fits the attribute's declared syntax.
4. **Syntax-driven generation** — a valid value for the attribute's syntax OID (integer, boolean, generalized time, telephone number, octet string, ...).
5. **Free text** — two lorem words, only for MUST attributes with no other source. A MAY attribute with an unsupported syntax is skipped instead.

## Formatter rules

- Templates use Bogus handlebars syntax: `{{dataset.method}}` or `{{dataset.method(args)}}`, case-insensitive. Text outside tokens is emitted verbatim. Browse the token catalog in the [Bogus documentation](https://github.com/bchavez/Bogus).
- Tokens must return scalar values (`{{lorem.word}}`, not `{{lorem.words}}`). Malformed tokens, non-scalar tokens, and templates that render empty **fail at generator construction** with a message naming the attribute and template — not at generation time, and never silently.
- Attribute keys are case-insensitive and alias-aware: a formatter keyed `"surname"` also applies to `sn`. Conflicting templates for two aliases of one attribute fail construction.
- A formatter overrides everything, including the example pool and the schema syntax check. **The template author owns validity**: `options.Formatters["uidNumber"] = "not-a-number"` is emitted as-is and the server rejects it at load.
- A formatter does **not** force a MAY attribute to appear. It only shapes the value when the attribute is generated. Raise `OptionalAttributeFill` (or put the attribute in a MUST class) to make it appear reliably.
- Output is deterministic per seed: tokens draw from the seeded generator, dates derive from a fixed epoch (2000-01-01) instead of the clock, and rendering is pinned to the invariant culture, so results do not vary by machine.
- RDN values must be unique under one parent. A too-narrow RDN formatter (few possible values) exhausts quickly; the generator retries 20 draws, then appends `-2`, `-3`, ... for string-like syntaxes and throws for structured ones (for example an INTEGER `uidNumber`). Give RDN formatters a wide value space, as `u{{randomizer.replacenumbers(######)}}` does.

## Things to know

- **`userPassword` can appear.** `inetOrgPerson` allows it, so the optional-fill dice can add a random plaintext password, which makes that entry bindable. Set `options.Formatters["userPassword"]` to control it, or treat generated people as searchable data only, as the built-in extensions do.
- **Bake the right base DN.** `WithSeedRecords` takes finished records; their DNs must end with the resource's base DN (`dc=example,dc=org` by default, or whatever `WithBaseDn` sets). Unlike `WithFakePeople`, the records do not re-parent when `WithBaseDn` appears later in the chain — read the base DN into a variable and use it in both places.
- **Parents must exist.** Declaring the OU with `WithOrganizationalUnit` also emits the base-DN root entry, so the tree above your records is complete. Without any typed helper you must include the root and OU entries in the records yourself.
- **Groups against a custom pool.** `LdifGenerator.Groups(count, parentDn, memberPool)` accepts any list of person records, including `SchemaEntryGenerator` output, and picks members from it. `LdifGeneratorOptions.DanglingMemberRatio` (0..1) makes a fraction of the member DNs point at entries that do not exist — useful to test consumers that must tolerate broken referential integrity.
- **Determinism contract.** Same seed + same options + same `LdifDotNet.Generator` package version ⇒ byte-identical output. A package upgrade may change the data; pin the version if your tests assert on specific values.
