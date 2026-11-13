# Stream Ended

YouTube や Twitch で自分の配信が終了したら、Windows に通知します。OBS で配信を止めたあと、映像を受け取る側の YouTube・Twitch に公式 API で問い合わせ、配信先でも終了を確認できます。OBS の表示に不具合があっても、配信先がライブと判定しているかを独立して確かめられます。チャンネルの登録は初回だけです。

## ダウンロード帯域の目安

| 確認方法 | 平均ダウンロード帯域 |
| --- | ---: |
| 視聴ページで配信を再生（受信ビットレートが 6 Mbps の例） | **6 Mbps** |
| Stream Ended で状態を確認（両サービスの API 応答が30秒あたり合計 10 KB の例） | **約 2.7 kbps** |

Stream Ended は映像や音声を受信せず、約30秒ごとに配信状態を取得します。数値は仮定に基づく計算例です。再生時の帯域は画質などで変わり、アプリの実際の通信量には接続や認証の通信も加わります。

## 起動

[Releases](https://github.com/kyoh86/stream-ended/releases) の **Assets** から `StreamEnded-Setup-x64.exe` をダウンロードして実行します。インストール先は現在のWindowsユーザー用で、管理者権限は不要です。サインイン時の自動起動は初期設定で有効です。インストール時のチェックボックス、またはインストール後に Windows の **設定 → アプリ → スタートアップ** で切り替えられます。

インストール後はスタートメニューの **Stream Ended** からも画面を開けます。更新するときは通知領域から **アプリを終了** して、新しいインストーラーを実行します。

Windows で `StreamEnded.exe` を実行すると、配信先ごとの状態とログインボタンが表示されます。ウィンドウを閉じても監視は続きます。通知領域のアイコンをクリックすると画面を開き直せます。監視を止めるにはアイコンを右クリックして **アプリを終了** を選びます。

- **YouTube でログイン** — ブラウザーで配信チャンネルを選んで認可します。YouTube ログイン対応のビルドを使ってください。
- **Twitch でログイン** — ブラウザーで Twitch にログインして認可します。Twitch ログイン対応のビルドを使ってください。

認証情報は Windows の現在のユーザーで暗号化して保存します。配信 URL の入力や OBS の設定は不要です。

## 通知

起動中は約30秒ごとに確認します。YouTube はライブを確認した配信枠が終了済みになったとき、Twitch はライブからオフラインへ切り替わったときに通知します。通知を見逃しても、メイン画面で確認時刻を見られます。API エラーの場合はメイン画面に確認失敗を表示します。

**今回の終了予定時刻**を設定した場合は、指定時刻を過ぎても配信中の配信先を一度ずつ通知します。全配信先の終了を確認するかアプリを終了すると、予定時刻は解除されます。

## GitHub Actions でリリースビルド

リポジトリの **Settings → Secrets and variables → Actions** で、次の Repository secrets を登録します。

- `YOUTUBE_OAUTH_JSON` — Google の「デスクトップ アプリ」用 OAuth クライアント JSON の全文
- `TWITCH_CLIENT_ID` — Twitch の **Public** クライアントの Client ID

**Actions → Release build → Run workflow** で実行します。成功した実行の **Artifacts** から `StreamEnded-win-x64` をダウンロードすると、インストーラーと単体の `StreamEnded.exe` が入っています。

GitHub Releases に配布する場合は、リリースしたいコミットにバージョンタグを付けて push します。

```sh
git tag v0.1.0
git push origin v0.1.0
```

CI が成功すると、[Releases](https://github.com/kyoh86/stream-ended/releases) の該当バージョンの **Assets** にインストーラーと単体の `StreamEnded.exe` が表示されます。ダウンロードには非公開リポジトリへのアクセス権が必要です。

ビルドに組み込んだ OAuth クライアント情報は実行ファイルから取り出せます。YouTube はデスクトップアプリ用、Twitch は Public クライアント用の値を使います。

## ビルド

.NET 10 SDK でビルドします。次のコマンドで Windows x64 用の実行ファイルを `publish` に出力できます。

```sh
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```


### YouTube ログイン対応版を作る（配布者向け）

Google Cloud で YouTube Data API v3 を有効にし、OAuth クライアントを「デスクトップ アプリ」として作成します。ダウンロードしたクライアント JSON はリポジトリの外に置きます。上の publish コマンドに `-p:StreamEndedYouTubeOAuthFile="/path/to/client.json"` を追加すると、配布用の実行ファイルにデスクトップクライアント情報が入ります。この情報は実行ファイルから取り出せるため、必ずデスクトップアプリ用のクライアントを使ってください。

テスト公開では、配布者が配信者の Google アカウントをテストユーザーに追加します。テスト時の認可は7日で失効します。一般配布に向けては OAuth 同意画面を本番用に設定し、Google の確認手続きを進めます。

### Twitch ログイン対応版を作る（配布者向け）

Twitch Developer Console でクライアントの種類を **Public** にしてアプリを登録します。publish コマンドに `-p:StreamEndedTwitchClientId=発行されたClientID` を追加します。配信者はメイン画面の Twitch 欄にある **ログイン** を押し、ブラウザーで認可します。連携にはデバイス認証を使用し、期限切れなどで認可を更新できなくなった場合は再ログインが必要です。

## Microsoft Store 版

Partner Center でアプリ名を予約し、**製品 ID** から次の値をリポジトリの **Settings → Secrets and variables → Actions → Variables** に登録します。同名の Repository secrets も使えます。

| Actions Variable | Partner Center の値 |
| --- | --- |
| `STORE_IDENTITY_NAME` | Package/Identity/Name |
| `STORE_PUBLISHER` | Package/Identity/Publisher（`CN=` を含む値） |
| `STORE_PUBLISHER_DISPLAY_NAME` | Package/Properties/PublisherDisplayName |

**Actions → Release build → Run workflow** で `store_version` に `1.0.0.0` のような4桁バージョンを入力します。既存のYouTube・Twitch用Secretsも使用します。成果物 **StreamEnded-Store-x64** 内の `StreamEnded-Store-x64.msix` を Partner Center の申請にアップロードします。MSIX は審査後に Microsoft Store が署名します。

申請では、`runFullTrust` の用途として Windows Forms の画面、通知領域の常駐とローカル設定保存を説明します。プライバシーポリシーのURLは https://kyoh86.dev/stream-ended/privacy/ です。審査で連携を試すには、Google OAuth を審査担当者が利用できる状態にしてください。

Store 版では自動起動を初期状態で無効にしています。アプリを一度開いたあと、Windows の **設定 → アプリ → スタートアップ** で有効にできます。自動起動時はメイン画面が開きます。
