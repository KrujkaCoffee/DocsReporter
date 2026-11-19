"""Offline reference-model checks + source invariants. NOT execution of C# or MSSQL.
Run: python -m unittest discover -s tests -p 'strict_access_offline.py' -v
The C# equivalent is tests/StrictAccess.Checks. Never report these as dotnet tests.
"""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def evaluate(case, verified_command=1):
    r=case['Resource']; principals=set(case['Principals']); rows=case['Rows']; groups=case['Groups']
    if not principals:return 'Denied'
    if verified_command is None or verified_command<=0:return 'Indeterminate'
    if r['ReferenceId']<=0 or r['ObjectId']<=0 or not r['Current']:return 'Denied'
    if case.get('Complete',True) is False:return 'Indeterminate'
    if r['State']!=0 or r['AccessLevel']!=0 or r['StageId']!=0 or r['MandatoryAccess']:return 'Indeterminate'
    if r['AccessObjectId']<=0:return 'Indeterminate'
    potential=[]
    for row in rows:
        if row['ReferenceId'] not in (0,r['ReferenceId']) and row['XReferenceId']!=r['ReferenceId']:continue
        conditional=row['AccessTypeId']!=2 or any(row[k]!=0 for k in ('XAuthorId','XReferenceId','XObjectId','XAccessObjectId','XReferenceAuthorGroupId','StageId','LinkTypeId','LinkId','Direction'))
        if not conditional and row['UserId']>0 and row['UserId'] not in principals:continue
        if not conditional and row['ObjectId']>0 and row['ObjectId'] not in (r['ObjectId'],r['AccessObjectId']):continue
        potential.append(row)
    if not potential:return 'Denied'
    allow=deny=False
    for row in potential:
        if row['UserId']<=0:return 'Indeterminate'
        if row['AccessTypeId']!=2 or row['ReferenceId']!=r['ReferenceId'] or row['ObjectId']<=0:return 'Indeterminate'
        if any(row[k]!=0 for k in ('StageId','LinkTypeId','LinkId','Direction','XAuthorId','XReferenceId','XObjectId','XAccessObjectId','XReferenceAuthorGroupId')):return 'Indeterminate'
        group=groups.get(str(row['AccessGroupId']))
        if not group or group['TypeId']!=2:return 'Indeterminate'
        read=group['Commands'].get(str(verified_command))
        if read is None:return 'Indeterminate'
        if read:allow=True
        else:deny=True
    return 'Denied' if deny else 'Allowed' if allow else 'Denied'


def supported_case():
    row=dict(AccessTypeId=2,AccessGroupId=4,UserId=3,ReferenceId=10,ObjectId=90,StageId=0,LinkTypeId=0,LinkId=0,Direction=0,XAuthorId=0,XReferenceId=0,XObjectId=0,XAccessObjectId=0,XReferenceAuthorGroupId=0)
    return dict(Resource=dict(ReferenceId=10,ObjectId=100,AccessObjectId=90,Current=True,State=0,AccessLevel=0,StageId=0,MandatoryAccess=False),Principals=[2,3],Rows=[row],Groups={'4':dict(TypeId=2,Commands={'1':True})})


class Model(unittest.TestCase):
    def test_unverified_read_profile(self):self.assertEqual(evaluate(supported_case(),None),'Indeterminate')
    def test_supported(self):self.assertEqual(evaluate(supported_case()),'Allowed')
    def test_snapshot(self):
        case=supported_case();case['Complete']=False
        self.assertEqual(evaluate(case),'Indeterminate')
    def test_unknown_mutations(self):
        for key in ('AccessTypeId','UserId','ObjectId','StageId','LinkTypeId','LinkId','Direction','XAuthorId','XReferenceId','XObjectId','XAccessObjectId','XReferenceAuthorGroupId'):
            with self.subTest(key=key):
                case=supported_case();case['Rows'][0][key]=7 if key=='AccessTypeId' else 0 if key in ('UserId','ObjectId') else 123
                self.assertNotEqual(evaluate(case),'Allowed')
    def test_unknown_order(self):
        case=supported_case();special=case['Rows'][0]|{'AccessTypeId':7,'UserId':0,'ObjectId':0}
        case['Rows'].append(special)
        self.assertEqual(evaluate(case),'Indeterminate');case['Rows'].reverse()
        self.assertEqual(evaluate(case),'Indeterminate')
    def test_no_command(self):
        case=supported_case();case['Groups']['4']['Commands']={}
        self.assertEqual(evaluate(case),'Indeterminate')
    def test_denied(self):
        case=supported_case();case['Groups']['4']['Commands']['1']=False
        self.assertEqual(evaluate(case),'Denied')
    def test_no_principal(self):
        case=supported_case();case['Principals']=[]
        self.assertEqual(evaluate(case),'Denied')
    def test_no_rule(self):
        case=supported_case();case['Rows']=[]
        self.assertEqual(evaluate(case),'Denied')
    def test_27_regressions(self):
        data=json.loads((ROOT/'tests/StrictAccess.Checks/strict-regression.json').read_text())
        sources={s['Id']:s for s in data['Sources']}
        cases=[case | {k:sources[case['SourceId']][k] for k in ('Rows','Groups')} for case in data['Cases']]
        self.assertEqual(len(cases),27)
        for case in cases:
            with self.subTest(case=case['Case']):self.assertEqual(evaluate(case),'Indeterminate')


class SourceInvariants(unittest.TestCase):
    def test_di(self):
        p=(ROOT/'Program.cs').read_text()
        self.assertIn('AddScoped<ITflexAccessDecisionService>(sp => sp.GetRequiredService<StrictReadService>()',p)
        self.assertNotIn('ITflexAccessDecisionService, TflexAccessDecisionService',p)
        self.assertLess(p.index('app.UseAuthentication()'),p.index('app.UseMiddleware<StrictPerimeterMiddleware>()'))
        self.assertLess(p.index('app.UseMiddleware<StrictPerimeterMiddleware>()'),p.index('app.UseStaticFiles()'))
    def test_preflight_before_query(self):
        p=(ROOT/'Reporter/Product/ProductExplorerService.cs').read_text()
        search=p[p.index('SearchAsync('):p.index('RootsAsync(')]
        self.assertLess(search.index('await RequireGroupAsync'),search.index('ScanAsync'))
        self.assertIn('await _referenceGate.RequireReadyAsync',p)
    def test_no_caption_fallback(self):
        p=(ROOT/'Reporter/StrictAccess/StrictAccessRules.cs').read_text()
        for value in ('IsReadGroup','IsHardDenyGroup','Просмотр','Редакторский','AccessGroupName'):
            self.assertNotIn(value,p)
    def test_no_debug(self):
        p=(ROOT/'Reporter/Infrastructure/ReporterPrincipalResolver.cs').read_text()
        self.assertNotIn('DebugWindowsLogin',p);self.assertIn('RuntimePrincipal.Read',p)
        p=(ROOT/'Reporter/Product/ProductPilotFilter.cs').read_text()
        self.assertNotIn('IsDevelopment()',p);self.assertNotIn('AllowAnonymousInDevelopment',p)
    def test_route_gate(self):
        p=(ROOT/'Reporter/StrictAccess/StrictPerimeterMiddleware.cs').read_text()
        self.assertIn('typeof(ReporterProductController)',p);self.assertIn('typeof(StrictSystemController)',p)
        self.assertIn('legacy_route_not_migrated',p)
        self.assertNotIn('StartsWith("/api/reporter',p)
    def test_publication_source_scoped(self):
        p=(ROOT/'Reporter/Services/ReporterAccessService.cs').read_text()
        self.assertNotIn('p.SourceId IS NULL',p);self.assertIn('ProvisionNewUsers',p)
        self.assertNotIn('MERGE app.AppUser',p)
    def test_audit_before_output(self):
        p=(ROOT/'Reporter/StrictAccess/RequestAudit.cs').read_text()
        self.assertLess(p.index('await _audit.ReadyAsync(status)'),p.index('await next()'))
        self.assertNotIn('Request.Headers',p);self.assertNotIn('Request.QueryString',p)
    def test_source_immutable(self):
        p=(ROOT/'Reporter/StrictAccess/StrictReadService.cs').read_text()
        self.assertIn('SERVERPROPERTY',p);self.assertIn('profile.ExpectedDatabase',p)
        self.assertIn('membership_limit_exceeded',p);self.assertIn('acl_snapshot_incomplete',(ROOT/'Reporter/StrictAccess/StrictAccessRules.cs').read_text())
    def test_audit_migration_own_db(self):
        p=(ROOT/'Reporter/sql/008b_strict_audit.sql').read_text()
        self.assertIn("OBJECT_ID(N'dbo.ParameterGroups'",p)
        self.assertNotIn('INSERT INTO dbo.',p);self.assertNotIn('UPDATE dbo.',p)
    def test_no_secrets_fixture(self):
        p=(ROOT/'tests/StrictAccess.Checks/strict-regression.json').read_text()
        for word in ('FullName','ActualName','s.dolgih','i.alekseev','v.polevoy','SRV-','ФИО'):
            self.assertNotIn(word,p)


if __name__=='__main__':unittest.main()
