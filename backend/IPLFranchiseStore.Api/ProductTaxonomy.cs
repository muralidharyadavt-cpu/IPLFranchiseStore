namespace IPLFranchiseStore.Api;

// Public catalogue vocabulary is separate from Product.Type, the persisted Cosmos discriminator.
public static class ProductTaxonomy
{
    public const string SignedPhotograph = "Signed Photograph";
    public static readonly string[] MerchandiseTypes = ["Jersey", "Cap", "Flag", SignedPhotograph, "Other"];
    private static readonly Dictionary<string, string> LegacySeedTypes = new(StringComparer.Ordinal)
    {
        ["mi-jersey"] = "Jersey", ["csk-cap"] = "Cap", ["rcb-flag"] = "Flag", ["kkr-signed-photo"] = SignedPhotograph
    };
    private static readonly (string Id, string Name, string[] Aliases)[] Franchises =
    [
        ("MI", "Mumbai Indians", []), ("CSK", "Chennai Super Kings", []),
        ("RCB", "Royal Challengers Bengaluru", ["Royal Challengers Bangalore"]),
        ("KKR", "Kolkata Knight Riders", [])
    ];

    public static string? NormalizeMerchandiseType(string value) =>
        value.Equals("Signed Photo", StringComparison.OrdinalIgnoreCase) ? SignedPhotograph :
        MerchandiseTypes.FirstOrDefault(type => type.Equals(value, StringComparison.OrdinalIgnoreCase));
    public static string LegacyType(string id) => LegacySeedTypes.GetValueOrDefault(id, "Other");
    public static string EffectiveType(Product product) => product.MerchandiseType is null ? LegacyType(product.Id)
        : NormalizeMerchandiseType(product.MerchandiseType) ?? product.MerchandiseType;
    public static Product Enrich(Product product) => product with { MerchandiseType = EffectiveType(product) };
    public static string[] LegacyIds(string merchandiseType) => LegacySeedTypes.Where(pair => pair.Value == merchandiseType).Select(pair => pair.Key).ToArray();
    public static string[] KnownLegacyIds() => LegacySeedTypes.Keys.ToArray();
    public static string FranchiseId(string value) => Franchises.FirstOrDefault(f => Matches(f, value)).Id ?? value;
    public static string FranchiseName(string value) => Franchises.FirstOrDefault(f => Matches(f, value)).Name ?? value;
    public static string[] FranchiseValues(string value)
    {
        var franchise = Franchises.FirstOrDefault(f => Matches(f, value));
        return franchise.Id is null ? [value] : new[] { franchise.Id, franchise.Name }.Concat(franchise.Aliases).ToArray();
    }
    private static bool Matches((string Id, string Name, string[] Aliases) franchise, string value) =>
        franchise.Id.Equals(value, StringComparison.OrdinalIgnoreCase) || franchise.Name.Equals(value, StringComparison.OrdinalIgnoreCase) ||
        franchise.Aliases.Contains(value, StringComparer.OrdinalIgnoreCase);
}
