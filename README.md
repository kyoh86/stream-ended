# Stream Ended

YouTube や Twitch で自分の配信が終了したら、Windows の通知領域に知らせます。公式 API で状態を確認するため、映像や音声は受信しません。チャンネルの登録は初回だけです。

## 起動

Windows で `StreamEnded.exe` を実行し、通知領域のアイコンを右クリックします。

- **YouTube を連携** — Google Cloud で YouTube Data API v3 を有効にして、種類が「デスクトップ アプリ」の OAuth クライアントを作成します。テスト公開の場合は配信用の Google アカウントをテストユーザーに追加します。ダウンロードしたクライアント JSON をアプリで選び、ブラウザーで配信チャンネルを選んで認可します。テスト公開の認可は期限が切れるため、必要に応じて再連携してください。
- **Twitch を登録** — Twitch Developer Console でアプリを登録し、チャンネルのログイン名、Client ID と Client Secret を入力します。

認証情報は Windows の現在のユーザーで暗号化して保存します。配信 URL の入力や OBS の設定は不要です。Windows ログイン時にも起動したい場合は、実行ファイルのショートカットを `shell:startup` に置きます。

## 通知

起動中は約30秒ごとに確認します。YouTube はライブを確認した配信枠が終了済みになったとき、Twitch はライブからオフラインへ切り替わったときに通知します。通知を見逃しても、トレイで確認時刻を見られます。API エラーの場合はトレイに確認失敗を表示します。

**今回の終了予定時刻**を設定した場合は、指定時刻を過ぎても配信中の配信先を一度ずつ通知します。全配信先の終了を確認するかアプリを終了すると、予定時刻は解除されます。

## ビルド

.NET 10 SDK でビルドします。次のコマンドで Windows x64 用の実行ファイルを `publish` に出力できます。

```sh
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
