# Stream Ended

YouTube や Twitch で自分の配信が終了したら、Windows の通知領域に知らせます。公式 API で状態を確認するため、映像や音声は受信しません。チャンネルの登録は初回だけです。

## 起動

Windows で `StreamEnded.exe` を実行すると、配信先ごとの状態とログインボタンが表示されます。ウィンドウを閉じても監視は続きます。通知領域のアイコンをクリックすると画面を開き直せます。監視を止めるにはアイコンを右クリックして **アプリを終了** を選びます。

- **YouTube でログイン** — ブラウザーで配信チャンネルを選んで認可します。YouTube ログイン対応のビルドを使ってください。
- **Twitch でログイン** — ブラウザーで Twitch にログインして認可します。Twitch ログイン対応のビルドを使ってください。

認証情報は Windows の現在のユーザーで暗号化して保存します。配信 URL の入力や OBS の設定は不要です。Windows ログイン時にも起動したい場合は、実行ファイルのショートカットを `shell:startup` に置きます。

## 通知

起動中は約30秒ごとに確認します。YouTube はライブを確認した配信枠が終了済みになったとき、Twitch はライブからオフラインへ切り替わったときに通知します。通知を見逃しても、メイン画面で確認時刻を見られます。API エラーの場合はメイン画面に確認失敗を表示します。

**今回の終了予定時刻**を設定した場合は、指定時刻を過ぎても配信中の配信先を一度ずつ通知します。全配信先の終了を確認するかアプリを終了すると、予定時刻は解除されます。

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
