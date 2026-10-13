using DocsApi.Reporter.Product;

var passed = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
void Throws(Action action, string name)
{
    try { action(); } catch (ArgumentException) { Check(true, name); return; }
    catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception("FAIL (expected exception): " + name);
}
var tdocs = new ProductReference(1080, "", "Link_891_403_1", "Изделие", 5, 891, 403);
var pkb = tdocs with { Id = 955, MasterId = 824, SlaveId = 404 };
var left = ProductExplorerRules.ResolveSide(tdocs, 891, 403);
Check(left.SourceColumn == "MasterID" && left.TargetColumn == "SlaveID", "tdocs typed forward");
Check(ProductExplorerRules.ResolveSide(pkb, 824, 404).TargetGroupId == 404, "PKB historical table name is not a reference id");
Check(ProductExplorerRules.ResolveSide(tdocs, 403, 891).SourceColumn == "SlaveID", "typed reverse");
Throws(() => ProductExplorerRules.ResolveSide(pkb, 891, 403), "wrong source metadata fails closed");
Throws(() => ProductExplorerRules.ResolveSide(tdocs, 891, 891), "self relation is not guessed");
Check(ProductExplorerRules.DirectionAllowed(left, "Both"), "Both allows a typed pass, not OR");
Check(!ProductExplorerRules.DirectionAllowed(left, "SlaveToMaster"), "policy direction restriction");
Check(!ProductExplorerRules.DirectionAllowed(left, "unexpected"), "unknown policy direction denied");
Check(ProductExplorerRules.ParsePath(null, 16).Length == 0, "empty root path");
Check(ProductExplorerRules.ParsePath("1,2,3", 3).SequenceEqual(new long[] { 1, 2, 3 }), "path parsed");
Throws(() => ProductExplorerRules.ParsePath("1,2,1", 16), "cycle in link path");
Throws(() => ProductExplorerRules.ParsePath("1,,2", 16), "empty path segment");
Throws(() => ProductExplorerRules.ParsePath("-1", 16), "negative path");
Throws(() => ProductExplorerRules.ParsePath("1e3", 16), "non integer path");
Throws(() => ProductExplorerRules.ParsePath("1,2,3", 2), "depth is server bounded");
Check(ProductExplorerRules.NodeKey("tdocs", 403, 1, new long[] { 11 }) !=
      ProductExplorerRules.NodeKey("pkb", 403, 1, new long[] { 11 }), "source belongs to node identity");
Check(ProductExplorerRules.NodeKey("tdocs", 403, 1, new long[] { 11, 21 }) !=
      ProductExplorerRules.NodeKey("tdocs", 403, 1, new long[] { 12, 21 }), "same occurrence under different displayed paths remains distinct");
var date = new DateTime(2026, 10, 12);
Check(ProductExplorerRules.AppliesAt(date, null, date), "start inclusive");
Check(!ProductExplorerRules.AppliesAt(null, date, date), "end exclusive");
Check(ProductExplorerRules.AppliesAt(null, null, date), "unbounded applicability");
Check(ProductExplorerRules.ParseDate("2026-10-12") == date, "fixed ISO date");
Throws(() => ProductExplorerRules.ParseDate("12.10.2026"), "ambiguous date rejected");
Check(ProductExplorerRules.EscapeLike("a%b_c[d~") == "a~%b~_c~[d~~", "literal search wildcard escaping");
Check(ProductSql.Q("a]b") == "[a]]b]", "quoted identifier");
Throws(() => ProductSql.Q(""), "empty identifier rejected");
var classes = new[] { new ProductClass(79, 0, 16), new ProductClass(80, 79, 16),
    new ProductClass(82, 0, 16), new ProductClass(110, 82, 16), new ProductClass(200, 0, 403) };
var folders = ProductExplorerRules.ExpandClasses(classes, new[] { 79 }, 16);
var files = ProductExplorerRules.ExpandClasses(classes, new[] { 82 }, 16);
Check(folders.SetEquals(new[] { 79, 80 }), "folder inheritance");
Check(ProductExplorerRules.ClassifyFile(79, folders, files) == "folder", "empty dotted-name folder is still a folder");
Check(ProductExplorerRules.ClassifyFile(110, folders, files) == "file", "file classification by local class");
Check(ProductExplorerRules.ClassifyFile(888, folders, files) == "unknown", "unknown class is not guessed from extension");
Throws(() => ProductExplorerRules.ExpandClasses(classes, new[] { 200 }, 16), "class belongs to another reference");
var profile = new ProductSourceProfile { StructureTypeIds = new[] { 1, 2 }, ExcludedProductClassIds = new[] { 696, 697 } };
var sql = ProductSql.HierarchyFilter(profile);
Check(sql.Contains("EXISTS") && !sql.Contains("JOIN dbo.UsingInStructures"), "structure types do not duplicate occurrences");
Check(sql.Contains("s_EndDate > @asOf") && sql.Contains("s_StartDate <= @asOf"), "SQL date boundary");
Check(sql.Contains("@st0,@st1") && sql.Contains("@ec0,@ec1"), "profile values are parameterized");
Check(!sql.Contains("1431") && !sql.Contains("NOLOCK"), "no captured client view or dirty read hint");
Check(ProductSql.ActiveRelation("l").Contains("DeletedChangelistID = 0") &&
      ProductSql.ActiveRelation("l").Contains("AddedClientViewID = 0"), "committed relation selection");
Throws(() => ProductSql.HierarchyFilter(new ProductSourceProfile()), "missing structure policy rejected");
Console.WriteLine($"TOTAL PASS: {passed}");
