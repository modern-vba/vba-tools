# VBA Tools リリース作業手順書

[English](release-operator-guide.md)

WindowsのPowerShell 7.2以降で、メンテナー自身がVBA Toolsをリリースするための手順です。
候補作成、レビュー、統合後commitの検証、クリーンなWindowsでのスモークテスト、
タグによる公開承認、公開物の検証までを扱います。
AIアシスタント、私的な補助スクリプト、過去のチャットセッションは不要です。

リリースの単位は拡張機能です。拡張機能のバージョンとMarketplaceチャネルは、
同梱CLI、言語サーバー、デバッグアダプターのバージョンとは独立しています。
公開方針と詳細なスモークテスト要件は[Release](release.md)、
ブランチとレビューの規則は[Contributing](../CONTRIBUTING.md)を参照してください。

## リリースの流れ

| 段階 | 行うこと | 次へ進むための条件 |
| --- | --- | --- |
| 準備 | バージョンを選び、リリース変更を生成する | バージョン、変更履歴、制約事項のレビュー完了 |
| レビュー | 候補を検証し、PRをmergeする | 正しい候補、検証合格、許可されたsquash merge |
| main検証 | 統合後commitでWindows Excelゲートを実行する | 正確なmain commit、cleanな状態、全ゲート合格 |
| スモークテスト | ローカル配布物を作り、使い捨てWindows VMで確認する | 必須のエディター、ブック、デバッグ確認が合格 |
| 公開承認 | 注釈付きリリースタグを作成し、pushする | タグが検証済みの正確なmain commitを指す |
| 公開 | タグのworkflowを確認する | Marketplaceに対象版が表示され、GitHub Releaseが公開済み |
| 公開物検証 | ダウンロードし、ハッシュと証明を確認する | immutableなReleaseと正確な配布物一式を検証済み |
| 受け入れ | Marketplaceからインストールし、動作確認する | verification noteと受け入れ条件の確認が完了 |

保護タグのpushは公開の承認です。リリース処理が動くか試すだけの目的でpushしないでください。
一度pushしたタグを移動、削除、再利用してはいけません。

## 開始前の準備

親のExcelワークスペースではなく、`modern-vba/vba-tools`のcleanなcloneまたはworktreeを使います。
Git、GitHub CLI、PowerShell 7.2以降、`.node-version`で指定されたNode.js、
`package.json`の`packageManager`で指定されたnpm、`global.json`で指定された正確な.NET SDKを用意します。
リリースを通すためにlocked restoreを解除したり、生成済み出力を編集したりしないでください。

検証ホストにはデスクトップ版ExcelとVBIDEへの信頼されたアクセスが必要です。
スモークテスト用に、使い捨てWindows VMまたは同等のクリーンな環境も用意します。
nativeゲート中はホストのVS Code、Excel、VBEを検証専用にし、
テスト用ブックを別の作業で操作・編集しないでください。

公開用identityとリポジトリ保護設定は、通常は初回に設定するものであり、リリースごとの作業ではありません。
公開処理はGitHub Actions OIDCとEntra managed identityを使います。
ローカルでのAzureサインインやMarketplace PATの入力は不要です。
新しいリポジトリの初期設定や設定監査は、
[Publishing identity setup](release.md#one-time-publishing-identity-setup)を参照してください。

コマンドは1つずつ実行します。外部プログラムでは`$LASTEXITCODE`を確認し、0以外なら停止してください。
`$ErrorActionPreference = 'Stop'`だけでは、すべてのnativeコマンドの失敗でPowerShellが停止するわけではありません。
ゲートや配布物作成と並行して次の段階を実行しないでください。

```powershell
git status --short --branch
git remote get-url origin
gh auth status
node --version
npm --version
dotnet --version
gh release verify --help
gh attestation verify --help
```

開始時のworktreeがcleanであること、GitHubアカウントとリポジトリが正しいこと、
ツールのバージョンが指定どおりであること、GitHub CLIが両方の証明検証コマンドに対応していることを確認します。
アクセストークンをリリースノートや検証記録にコピーしてはいけません。

## 1 リリース入力を決める

リポジトリルートで次の変数を設定します。`0.1.7`は新しいpre-releaseの例であり、公開の指示ではありません。
実際の作業では未使用のバージョンを選びます。
CLIの新バージョンを意図してリリースする場合を除き、`$CliVersion`は変更しません。

```powershell
$Repo = 'modern-vba/vba-tools'
$Version = '0.1.7'
$Channel = 'pre-release'
$CliVersion = '0.1.0'
$Tag = "vba-tools-v$Version"
$VsixName = "vba-tools-win32-x64-$Version.vsix"
$CliName = "vba-dev-win-x64-$CliVersion.zip"
$Branch = "codex/issue-246-release-$Version"
$ReleaseIssue = 246
$RunStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Evidence = Join-Path (Get-Location) ".tmp/release-operator/$Tag-$RunStamp"
```

`pre-release`は奇数のminor version、`stable`は偶数のminor versionを使います。
stableとpre-releaseには、それぞれ異なる数値バージョンが必要です。
リリースIssueは現在の作業に対応するものを使い、常にIssue #246が適切だとは考えないでください。
現在のPR検証workflowは、head branch名が`codex/issue-246-release-`で始まる場合だけ実行されます。
このブランチ方針を変える場合は、レビューを通してworkflowも変更してください。
検証jobのskipは合格ではありません。このprefixはworkflowの実行条件であり、Issueの変更やcloseを許可するものではありません。

## 2 候補を準備してレビューする

現在のmainから開始し、このブランチに未完了の別作業を積み重ねないようにします。

```powershell
git fetch origin main --tags
if ($LASTEXITCODE -ne 0) { throw 'Main and tag refresh failed.' }
git switch -c $Branch origin/main
npm ci --ignore-scripts
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
npm run release:prepare -- --extension-version $Version --channel $Channel --vba-dev-version $CliVersion
if ($LASTEXITCODE -ne 0) { throw 'Release preparation failed.' }
git diff --check
git diff --stat
git diff
```

生成された差分をレビューします。`package.json`、`package-lock.json`、ルートの変更履歴で、
拡張機能のバージョンが一致している必要があります。
CLIのバージョンと履歴の変更は、意図した独立CLIリリースの範囲に限定します。
要件、破壊的変更、既知の制約、同梱ツールのバージョン、リンク、アイコン、サポート情報も確認してください。
生成コマンドはcommit、タグ作成、push、公開を行いません。

レビュー済みのリリースファイルだけをstageし、候補ゲートの実行前にcommitします。
ローカル検証記録、認証情報、ダンプ、無関係な変更はstageしないでください。
次は、記載されたパスをレビューした後に実行する例です。

```powershell
git add package.json package-lock.json CHANGELOG.md tools/vba-dev/Directory.Build.props tools/vba-dev/CHANGELOG.md
git commit -m "chore(release): prepare $Version" -m "Prepare the reviewed extension version, channel, and release notes."
git status --short
npm run verify:release:windows-excel
if ($LASTEXITCODE -ne 0) { throw 'Candidate Windows Excel release gate failed.' }
git status --short
```

この全体ゲートには、通常のリリース検証とnative Excelのテスト群が含まれます。
数十分かかることがあり、ExcelやVBEのウィンドウを使用します。
Excelの前提不足、必須native suiteのskip、出力の未回収、中断した実行は合格ではありません。
診断用profileはリリースゲートの代用になりません。

候補SHAと結果をPRへ記録します。ブランチをpushし、リリースPRを作成します。

```powershell
$CandidateCommit = (git rev-parse HEAD).Trim()
git push -u origin $Branch
gh pr create --repo $Repo --base main --head $Branch --draft --title "chore(release): prepare $Version" --body 'Release preparation. Add the reviewed versions, verification evidence, and a non-closing issue reference before review.'
```

GitHub上でPR本文に`Refs #<release issue>`とゲートの検証記録を追加します。
**Verify release preparation**が、その候補SHAで実際に実行され、合格したことを確認してください。
ローカルとhostedの両検証が合格してからPRのdraftを解除します。
レビューとsquash mergeは`CONTRIBUTING.md`に従います。
通常のPRには有効なapprovalが必要です。メンテナー自身が作成したPRの限定的なadmin bypass経路も、
対象PRを特定した明示的な判断が必要です。自己approveしたり、管理者権限の保有だけでレビューを省略したりしてはいけません。

## 3 統合後のmain commitを再検証する

merge後は、現在の`origin/main`をcleanな状態でcheckoutします。
squash merge後のcommitは、treeが候補と同じでもcommitのidentityが異なります。

```powershell
git fetch origin main --tags
if ($LASTEXITCODE -ne 0) { throw 'Main and tag refresh failed.' }
git switch --detach origin/main
$VerifiedCommit = (git rev-parse HEAD).Trim()
git status --short
npm ci --ignore-scripts
if ($LASTEXITCODE -ne 0) { throw 'Main dependency restore failed.' }
New-Item -ItemType Directory -Path $Evidence -ErrorAction Stop | Out-Null
npm run verify:release:windows-excel 1> (Join-Path $Evidence 'main-gate.stdout.log') 2> (Join-Path $Evidence 'main-gate.stderr.log')
if ($LASTEXITCODE -ne 0) { throw 'Integrated main Windows Excel gate failed.' }
git status --short
git rev-parse HEAD
```

両方のログを読み、すべてのnative stageが実行され、合格したことを確認します。
完全なSHA、開始前と終了後のcleanな状態、終了結果をまとめて残してください。
部分的なログの最終行やworkflow全体のバッジだけで合格と判断してはいけません。

意図的にプロセスのaffinityなどの環境条件を設定した場合は、その条件を結果とともに記録し、実行後に復元します。
製品・テストプロセスへの制約の一環として、Excelのaffinityを変更しないでください。

## 4 スモークテスト用のローカル配布物を作る

新しい出力ディレクトリに一度だけ組み立てます。
このコマンド自身も通常の非Excel前提検証を実行します。
検証を省略したり、別の組み立てを同時実行したりしないでください。

```powershell
$LocalAssets = Join-Path $Evidence 'local-assets'
npm run release:artifacts -- --extension-version $Version --channel $Channel --vba-dev-version $CliVersion --output $LocalAssets
if ($LASTEXITCODE -ne 0) { throw 'Local artifact assembly failed.' }
node scripts/releaseWorkflow.mjs validate-assets-static --directory $LocalAssets --extension-version $Version --channel $Channel --vba-dev-version $CliVersion
if ($LASTEXITCODE -ne 0) { throw 'Local artifact validation failed.' }
Get-ChildItem -LiteralPath $LocalAssets -File | Get-FileHash -Algorithm SHA256
```

出力は`$VsixName`、`$CliName`、`SHA256SUMS`の3ファイルだけです。
validatorは、バージョン、target、チャネル、checksum、package contract、
VSIX同梱CLIとstandalone ZIP内のCLIの完全な同一性を確認します。

これらは公開前のスモークテスト用ファイルです。
タグのworkflowは、正式なクラウド配布物を別途一度だけ作成します。
ローカル版とクラウド版のVSIXのハッシュが一致するとは限りません。
ローカルのスモークテスト用ファイルでクラウド配布物を上書きしてはいけません。

## 5 クリーンなWindows用の検証fixtureを準備する

インポート、Buildによる上書き保存、保存せず終了を行ってよい、使い捨てのブック付きサンプルだけを使います。
`vba-project.json`を確認し、`templatePath`が選ぶsource workbookを特定してください。
古い`bin`ブックは実行対象ではありません。
サンプル内のテストとデバッグ用procedureは、明示的な保存や外部への副作用を行わない無害なものにします。

1. サンプルprojectと3つのローカル配布物を含む、新しいkitを作成します。
   fixtureはコピーし、元のauthoring sourceは移動・編集しません。
2. サンプルが隣接する`common_modules_repo`を参照する場合は、レビュー済みのclosed flat packageと
   canonical manifestを、参照どおりの相対位置に含めます。任意のsourceファイルから再構成してはいけません。
3. ファイルシステムのリンク、secret、エディターprofile、認証情報、無関係なブックを除外します。
   コピー元とコピー先の相対パス、サイズ、SHA-256を記録し、コピー後も元のinventoryが不変であることを確認します。
4. kit内だけで、既存の`.vscode/launch.json`を`launch.saved.json`として保全し、dynamic F5を先に確認できるようにします。
   同名の保存済み設定が既にある場合は上書きしないでください。
5. 既存設定を保全したうえで、このfixture内のAuto Saveと文字コードの自動推測を無効にします。
   Shift_JISで書き出されたVBAの場合、該当設定は次のとおりです。

   ```json
   {
     "files.autoSave": "off",
     "files.autoGuessEncoding": false,
     "[vba]": { "files.encoding": "shiftjis" }
   }
   ```

   fixtureの実際の文字コードに合わせてください。UTF-8のsourceにShift_JISを強制してはいけません。
6. kitをVMへコピーし、コピーした全ファイルのハッシュをホストのinventoryと照合します。
   kitルートと隣接packageの配置を維持してください。
7. VMで**Extensions: Install from VSIX**を使い、コピーした正確なVSIXをインストールします。
   求められたらVS Codeを再読み込みし、インストールされたバージョンを確認します。
8. レビュー済みの検証フォルダーだけをTrustします。
   ExcelのVBA projectへの信頼されたアクセスと、ブック固有のコンテンツ確認は、このレビュー済みfixtureに対して設定します。
   セキュリティを全体で無効にしたり、将来のすべてのフォルダーを信頼したりしてはいけません。

以降のVM用コマンドは、ビルドホストではなくVM内で実行します。
VMの検証記録とホストの検証記録は分けてください。
self-containedな同梱ツールを動かすためだけに、追加の.NET runtimeをインストールしてはいけません。

## 6 VMのスモークテストを実行して記録する

詳細は[Clean Windows Smoke](release.md#clean-windows-smoke)の全要件に従います。
作業時のチェックポイントは次のとおりです。

| 確認 | 合格条件 |
| --- | --- |
| 言語機能 | 書き出されたVBAで補完、診断、symbolが動作する |
| 整形 | keywordとindentが期待どおり変わり、commentとstringが正しく保持され、元のfixture sourceが保全される |
| DoctorとHost Events | Doctorのnative確認が合格し、warningを確認済み。Refresh UserForm Eventsがsource templateを開いたり保存したりせずcatalogを報告する |
| 閉じたブックでBuild | manifestが選ぶsource workbookへimport・保存し、そのhidden sessionを閉じる |
| 開いたブックでBuild | 既存のExcel PID/windowを維持し、同意後のimport・Saveで意図的な無害な未保存セルmarkerも保存する |
| 閉じたブックと開いたブックでTest | 正確なsource workbookを使用し、既存のopen sessionを開いたままにする。Test自身はSaveしない |
| Test Explorer | nodeが検出され、既定のRun Tests profileがブックを自動保存せず成功する |
| Dynamic F5 | launch.jsonがない状態で、未保存sourceのprocedureと通常の行breakpointを使い、正確なsource workbookのnative VBE Break modeに入る |
| ContinueとStop | Continueで無害なprocedureを実行でき、Stopでnative Resetを行う。ブックは保存せず開いたまま維持する |
| Restartと保存済み設定のF5 | 同じExcel PID/windowと未保存セルmarkerを維持し、Restartで最新の未保存sourceをimportする。保存済みtargetも動作する |
| 最終比較 | 使い捨てブックだけを保存せず閉じ、エディターの未保存変更を破棄した後、sourceとブックのハッシュが意図したbaselineと一致する。追加は意図したlaunch.jsonや別の検証記録だけ |
| インストール済み同梱ツール | 3つの実行ファイルのハッシュが、実際にインストールしたVSIXのentryと一致する。installed LSPの直接実行で独立管理のバージョンが表示される |

Buildは意図的に保存するため、**Build後に**ディスク上のbaselineを記録し直します。
次のBuildより前に、Testの結果をそのbaselineと比較してください。
デバッグでは無害なdebug moduleをBuildで準備し、別のbaselineを記録して、
最後に保存せず閉じた後で比較します。生成された`launch.json`は意図した追加として保持しますが、
それを理由にほかの予期しない変更を許容してはいけません。

nativeデバッグのスモークテストには、`Option Private Module`を持つ標準moduleと、
引数のないpublicな`Sub`を使います。未保存のエディター内容は、自動保存せずcaptureされる必要があります。
Testではdirty workbookへの同意が求められる場合がありますが、DebugのF5とRestartではその確認を使いません。
VBA自身は保存や副作用を起こせるため、fixtureのコードでは行わないようにします。

インストール済み実行ファイルの同一性確認では、`$VmVsix`にコピーしたVSIX、
`$ExtensionRoot`にVS Codeで確認した実際の有効なインストール先を指定します。
複数バージョンがある場合、更新日時が最新という理由だけで選ばないでください。

```powershell
$VmVsix = Read-Host 'Full path of the VSIX installed in this VM'
$ExtensionRoot = Read-Host 'Full path of the active modern-vba.vba-tools installation'
$InstalledPackage = Get-Content -LiteralPath (Join-Path $ExtensionRoot 'package.json') -Raw | ConvertFrom-Json
if ($InstalledPackage.version -ne $Version) { throw 'Wrong installed extension version.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Archive = [IO.Compression.ZipFile]::OpenRead($VmVsix)
try {
    foreach ($Relative in @(
        'bin/vba-dev/win-x64/vba-dev.exe',
        'bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe',
        'bin/vba-language-server/win-x64/vba-language-server.exe'
    )) {
        $Entry = $Archive.GetEntry("extension/$Relative")
        if ($null -eq $Entry) { throw "Missing VSIX entry: $Relative" }
        $Stream = $Entry.Open()
        $Hasher = [Security.Cryptography.SHA256]::Create()
        try { $ExpectedHash = [Convert]::ToHexString($Hasher.ComputeHash($Stream)) }
        finally { $Stream.Dispose(); $Hasher.Dispose() }
        $ActualHash = (Get-FileHash -LiteralPath (Join-Path $ExtensionRoot $Relative) -Algorithm SHA256).Hash
        if ($ActualHash -cne $ExpectedHash) { throw "Installed binary mismatch: $Relative" }
        "$Relative $ActualHash"
    }
}
finally { $Archive.Dispose() }
& (Join-Path $ExtensionRoot 'bin/vba-language-server/win-x64/vba-language-server.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'Installed LSP direct execution failed.' }
```

先にVMのshellへ、選択した`$Version`の値を設定してください。
LSPの表示は拡張機能のバージョンではなく、LSP自身のリリースmetadataと比較します。
クリーンな環境のruntime条件は、次のコマンドで記録します。

```powershell
Test-Path -LiteralPath 'C:\Program Files\dotnet\shared'
Test-Path -LiteralPath 'C:\Program Files (x86)\dotnet\shared'
Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\shared')
Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object Source
```

標準パスがFalseでPATH上に`dotnet.exe`がないことは、すべての独自runtime配置の不存在を証明するものではありません。
確認した事実どおりに記録してください。タグ作成前に必須のスモークテストを完了します。
方針上許可される`not-required`にも、その方針が求める理由が必要です。
未完了の必須確認を置き換えるものではありません。

## 7 タグの作成とpushで公開を承認する

ホストの正確な検証済みmain checkoutへ戻ります。保護設定を変更せず、公開設定を確認します。

```powershell
node scripts/releaseWorkflow.mjs validate-protections
if ($LASTEXITCODE -ne 0) { throw 'Release protection preflight failed.' }
$ImmutableSetting = gh api "repos/$Repo/immutable-releases" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $ImmutableSetting.enabled -ne $true) { throw 'Immutable releases must be enabled.' }
git fetch origin main --tags
if ($LASTEXITCODE -ne 0) { throw 'Main and tag refresh failed.' }
if ((git rev-parse HEAD).Trim() -ne $VerifiedCommit) { throw 'Verified checkout changed.' }
if ((git rev-parse origin/main).Trim() -ne $VerifiedCommit) { throw 'Main moved; reverify the new exact commit before tagging.' }
if (git status --porcelain) { throw 'Tagging requires a clean checkout.' }
```

管理者として、GitHubの**Settings > Rules > Rulesets**で有効なrelease tag作成rulesetと
tag不変保護rulesetを確認し、bypass actorも正確に監査します。削除と強制更新の保護を維持してください。
`marketplace-release` Environmentが、意図した保護タグだけに制限されていることも確認します。
この確認を通すために保護を弱めてはいけません。

全体ゲートとVMの合格結果を、`$VerifiedCommit`に紐付けてリリースPRまたはIssueへ記録します。
その後、公開を意図的に承認して次を実行します。

```powershell
npm run release:tag -- --version $Version --channel $Channel --vba-dev-version $CliVersion --verified-commit $VerifiedCommit --windows-excel-result pass --clean-windows-smoke pass
if ($LASTEXITCODE -ne 0) { throw 'Release tag creation failed.' }
git cat-file -p $Tag
git rev-parse "$Tag^{commit}"
git push origin $Tag
if ($LASTEXITCODE -ne 0) { throw 'Tag push was not confirmed; inspect remote state before any further action.' }
```

push前にannotationを確認します。チャネル、完全な検証SHA、Windows Excel結果、
クリーンなWindowsスモーク結果が正しい必要があります。
方針上スモークテストを省略できる場合は、`--clean-windows-smoke not-required`と
`--clean-windows-smoke-reason`を併用し、未実施の確認を`pass`と記録してはいけません。
タグコマンドはローカルに注釈付きタグを作るだけであり、pushによって公開処理が始まります。

## 8 公開処理を確認する

正確なタグpushのrunを探し、`headSha`が`$VerifiedCommit`と一致することを確認します。
別のrunを起動せず、そのrunを監視してください。

```powershell
gh run list --repo $Repo --workflow release.yml --event push --branch $Tag --json databaseId,headSha,status,conclusion,url
$ReleaseRun = Read-Host 'Database ID of the exact tag-push run'
gh run watch $ReleaseRun --repo $Repo --interval 300 --exit-status
gh run view $ReleaseRun --repo $Repo --json headSha,event,status,conclusion,jobs,url
```

workflowはタグ検証、hosted検証、クラウド配布物作成、内容確認と証明作成、draft作成、
Marketplace公開、GitHubの正式公開を行います。
初回用とresume用の相互排他的な分岐は、設計上どちらかがskipされる場合がありますが、
必須の公開処理と最終処理は実際に完了していなければなりません。
全体のバッジが緑色というだけではリリース受け入れ完了ではありません。

必須jobの失敗、skip、停止、操作待ちがあれば、既存のタグ、draft、配布物を保持します。
別のビルド、タグの再push、配布物の置き換え、公開の繰り返しを行わず、まず現在の状態を確認してください。
[既存draftの正式公開](#既存draftの正式公開)は、Marketplace公開が既に完了しており、
正確な検証済みGitHub draftだけを公開する必要がある、限定された場合の手順です。

## 9 公開済み配布物を独立に検証する

タグが指す正確なsource checkoutで、locked dependenciesを使って実行します。
新しいディレクトリを用意してください。validatorはちょうど3ファイルだけを要求するため、
検証結果の出力は配布物ディレクトリの外に保存します。

```powershell
node scripts/releaseWorkflow.mjs verify-live-marketplace --extension-version $Version --channel $Channel
if ($LASTEXITCODE -ne 0) { throw 'Exact Marketplace version, channel, and target are not visible.' }
$PublicRelease = gh api "repos/$Repo/releases/tags/$Tag" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Published GitHub Release was not found.' }
if ($PublicRelease.draft -or -not $PublicRelease.immutable -or $PublicRelease.tag_name -cne $Tag) { throw 'Release is not the expected published immutable release.' }
if ($PublicRelease.prerelease -ne ($Channel -eq 'pre-release')) { throw 'Published channel mismatch.' }
gh release verify $Tag --repo $Repo --format json
if ($LASTEXITCODE -ne 0) { throw 'Release attestation verification failed.' }
$PublicAssets = Join-Path $Evidence 'public-assets'
New-Item -ItemType Directory -Path $PublicAssets -ErrorAction Stop | Out-Null
gh release download $Tag --repo $Repo --dir $PublicAssets
if ($LASTEXITCODE -ne 0) { throw 'Public asset download failed.' }
node scripts/releaseWorkflow.mjs validate-assets-static --directory $PublicAssets --extension-version $Version --channel $Channel --vba-dev-version $CliVersion
if ($LASTEXITCODE -ne 0) { throw 'Public asset static validation failed.' }
foreach ($AssetName in @($VsixName, $CliName)) {
    gh attestation verify (Join-Path $PublicAssets $AssetName) --repo $Repo --signer-workflow modern-vba/vba-tools/.github/workflows/release.yml --source-ref "refs/tags/$Tag" --source-digest $VerifiedCommit --deny-self-hosted-runners
    if ($LASTEXITCODE -ne 0) { throw "Build provenance verification failed: $AssetName" }
}
Get-ChildItem -LiteralPath $PublicAssets -File | Get-FileHash -Algorithm SHA256
git ls-remote --tags origin "refs/tags/$Tag" "refs/tags/$Tag^{}"
```

remoteの注釈付きtag objectと、参照先を解決したcommitが、pushしたタグと一致することを確認します。
検証済みrelease attestation内のrepository/tag identity、Release ID、tag object digest、
3つのasset digestを、Release metadataとダウンロードしたファイルのハッシュに照合します。
build provenanceは、さらにVSIXとZIPを`release.yml`、正確なtag ref、`$VerifiedCommit`に紐付けます。

コマンドの結果、Release ID、asset IDとdigest、tag object SHA、run URL、公開URLを検証記録に残します。
この静的検証で、ダウンロードしたEXEを実行してはいけません。
以前のVM用ハッシュやstaging jobの成功を、公開ファイルの検証の代わりにしないでください。

## 10 Marketplaceからインストールして受け入れを完了する

検証マシンでVS Codeを閉じてから、ローカルのスモークテスト用VSIXではなく、
Marketplace経由で正確な公開バージョンをインストールします。

```powershell
$InstallArguments = @('--install-extension', "modern-vba.vba-tools@$Version", '--force')
if ($Channel -eq 'pre-release') { $InstallArguments += '--pre-release' }
code @InstallArguments
if ($LASTEXITCODE -ne 0) { throw 'Marketplace installation failed.' }
code --list-extensions --show-versions
```

VS Codeを再読み込みし、有効なバージョンを確認します。
使い捨てサンプルでactivation、言語機能、整形、Doctor、Build、Test/Test Explorer、デバッグを確認してください。
前述のVM同一性確認で、`$VmVsix`にダウンロードした**公開版**VSIXを指定し、
インストール済み同梱ツールを比較します。ローカルビルドと同じだと仮定せず、公開版のインストールを確認するためです。
MarketplaceのREADME、GitHub Releaseのリンクとダウンロードも確認します。

リリースIssueとPRに結果を記録します。verification noteには次を含めてください。

- バージョン、チャネル、platform、正確なtag objectとcommit。
- 統合経路、PR、レビュー、または対象を限定した明示的なbypass承認。
- 候補とhostedの検証、正確なmainでのnativeゲート、クリーンなWindowsスモーク。
- 公開済みRelease ID/URL、immutable状態、3つのasset ID/ハッシュ、release attestation、両方のbuild provenance結果。
- Marketplaceの正確なバージョンのインストールと、観測した動作。
- 既知の制約、明示的に受け入れた例外、残作業。

機密ログ、私的パス、認証情報、ダンプを公開記録に含めないでください。
既知リスクを受け入れる場合は、未証明の原因、修正、無条件の安定性を主張せず、観測内容とメンテナーの判断を記録します。
レビュー済みのリリースノート補足は、配布物、タグ、チャネル、保護設定を変えずにRelease本文へ追記できます。

現在の`origin/main`ですべての受け入れ条件を検証した場合、または残る各条件をメンテナーが明示的に対象外とした場合だけ、
リリースIssueをcloseしてProject itemをDoneにします。公開できたことだけでは完了ではありません。

## 既存draftの正式公開

対象のMarketplaceバージョン・チャネル・targetが既に表示され、実行中のrelease jobがなく、
保持されたGitHub draftに元の証明付き配布物一式が揃っている場合だけ使います。
この手順はそのdraftの公開だけを行い、再ビルド、Marketplace再公開、タグや配布物の変更は行いません。
メンテナーが対象のRelease IDを指定して、意図的に実行を選ぶ必要があります。

1. 一意のdraftを取得し、そのidentityを記録します。

   ```powershell
   $DraftJson = Join-Path $Evidence 'draft-before.json'
   node scripts/releaseWorkflow.mjs resolve-draft --tag $Tag --release-json $DraftJson
   if ($LASTEXITCODE -ne 0) { throw 'Unique exact draft lookup failed.' }
   $Draft = Get-Content -LiteralPath $DraftJson -Raw | ConvertFrom-Json
   $Draft.id
   $Draft.assets | Select-Object id,name,size,digest
   $Draft.body
   $DraftSnapshotHash = (Get-FileHash -LiteralPath $DraftJson -Algorithm SHA256).Hash
   ```

   ハッシュだけでなく、完全なsnapshotを保持してください。
   `git ls-remote`で正確なtag objectと参照先commitを確認し、title/channelが意図どおりで、
   asset ID、サイズ、digestが変わっていないことを確認します。
   draftの検索に、公開済みRelease用の`/releases/tags/{tag}` endpointを使ってはいけません。
2. 3配布物を新しい`$DraftAssets`ディレクトリへダウンロードして検証します。

   ```powershell
   $DraftAssets = Join-Path $Evidence 'draft-assets'
   New-Item -ItemType Directory -Path $DraftAssets -ErrorAction Stop | Out-Null
   gh release download $Tag --repo $Repo --dir $DraftAssets
   if ($LASTEXITCODE -ne 0) { throw 'Draft asset download failed.' }
   node scripts/releaseWorkflow.mjs validate-draft --release-json $DraftJson --directory $DraftAssets --tag $Tag --title "VBA Tools $Version" --extension-version $Version --channel $Channel --vba-dev-version $CliVersion --vsix-name $VsixName --cli-archive-name $CliName
   if ($LASTEXITCODE -ne 0) { throw 'Draft metadata or artifact validation failed.' }
   foreach ($AssetName in @($VsixName, $CliName)) {
       gh attestation verify (Join-Path $DraftAssets $AssetName) --repo $Repo --signer-workflow modern-vba/vba-tools/.github/workflows/release.yml --source-ref "refs/tags/$Tag" --source-digest $VerifiedCommit --deny-self-hosted-runners
       if ($LASTEXITCODE -ne 0) { throw "Draft provenance verification failed: $AssetName" }
   }
   node scripts/releaseWorkflow.mjs verify-live-marketplace --extension-version $Version --channel $Channel
   if ($LASTEXITCODE -ne 0) { throw 'Exact Marketplace package is not visible.' }
   ```

   ダウンロードしたファイルのハッシュを、`SHA256SUMS`だけでなく各draft assetのAPI digestとも照合します。
   手順7と同様にimmutable releasesが有効であることと、保護タグのrulesetも再確認してください。
   metadata、checksum、provenanceに不足や変更があれば停止し、代わりのファイルや証明を作成してはいけません。
3. 公開直前に`gh api "repos/$Repo/releases/$($Draft.id)"`で再取得し、
   ID、tag、title、draft/channel状態、本文全体、asset inventoryを保存済みsnapshotと照合します。
   注釈付きタグの参照先も変わっていないことを再確認してください。
   すべての条件が一致し、対象IDの公開を承認した場合だけ実行します。

   ```powershell
   $Confirmation = Read-Host "Type $($Draft.id) to publish only the verified $Tag draft"
   if ($Confirmation -cne [string]$Draft.id) { throw 'Draft publication not authorized.' }
   gh release edit $Tag --repo $Repo --draft=false
   if ($LASTEXITCODE -ne 0) { throw 'Publication outcome needs inspection; do not repeat it automatically.' }
   ```

   既存のpre-release状態と本文は変更しません。
   要求が失敗した場合や結果が不明な場合は、次の判断の前に同じRelease IDの状態を読み直してください。
   Releaseの再作成や、状態を確認しない再試行を行ってはいけません。
4. 新しい公開物をダウンロードして手順9を完了し、公開済みasset ID/digestを保持したdraftと比較します。
   non-draftかつimmutableであること、release attestation、両方の元のbuild attestation、
   tagの紐付けが不変であることを確認します。その後、Marketplaceインストールと受け入れ記録へ進みます。

## 最終チェックリスト

- [ ] バージョン、チャネル、独立管理の同梱ツールをレビューした。
- [ ] 候補を検証し、許可されたレビュー経路で統合した。
- [ ] 統合後mainの正確なSHAで、Windows Excel全体ゲートが合格した。
- [ ] 使い捨てfixtureで、必須のクリーンなWindowsスモークを完了した。
- [ ] 注釈付き保護タグを意図的に承認し、一度だけpushした。
- [ ] Marketplaceに正確なバージョン、target、チャネルが表示されている。
- [ ] 同じGitHub Releaseが公開済み・immutableで、証明を検証した。
- [ ] 新たに取得した公開VSIX、CLI ZIP、SHA256SUMSを検証し、ハッシュを記録した。
- [ ] 両方のbuild attestationが、正確なworkflow、tag ref、commitに紐付いている。
- [ ] Marketplaceの正確なバージョンをインストールし、動作を受け入れた。
- [ ] レビュー記録に結果、既知の制約、残作業を記載した。
- [ ] 単なる公開完了ではなく、受け入れ確認に従ってIssue/Projectを完了した。

## コマンドと方針の参照先

- [リリース方針と自動化contract](release.md)
- [開発参加と統合の規則](../CONTRIBUTING.md)
- [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)
- [GitHub CLIのrelease検証](https://cli.github.com/manual/gh_release_verify)
- [GitHub CLIのbuild attestation検証](https://cli.github.com/manual/gh_attestation_verify)
- [VS Codeのコマンドラインインストール](https://code.visualstudio.com/docs/configure/command-line#working-with-extensions)
