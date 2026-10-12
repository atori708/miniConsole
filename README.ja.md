# miniConsole

[![Release](https://img.shields.io/github/v/release/Atori708/miniConsole)](https://github.com/Atori708/miniConsole/releases)
![Unity_Version](https://img.shields.io/badge/unity-6000.0+-blue)
[![License](https://img.shields.io/badge/license-MIT-green)](Packages/com.atori708.miniconsole/LICENSE.md)
[![Readme_EN](https://img.shields.io/badge/readme-English-red)](README.md)

Unity 標準の Console を、必要な機能だけに絞ったシンプルな Console ウィンドウ。

| Console (標準) | miniConsole |
|---|---|
| ![Console](Documentation~/images/console.jpg) | ![miniConsole](Documentation~/images/miniconsole.jpg) |

`Window > General > miniConsole` から開く。

## 標準の Console との違い

- **シンプルな機能だけを持つ Console**
- **ログ一覧は 1 行表示**なので、一度にたくさんのログを表示できる
- **ログの詳細は必要最低限**。スタックトレースは「ファイル名:行番号」だけを表示するので、リンクをクリックしてコードに飛びやすい
- **標準の Console にあって miniConsole に無い機能**
  - Editor 以外のログには対応していない (実機など、接続先の Player のログは表示されない)
  - Error Pause は不要な機能と判断し、あえて対応していない

## 自前ロガーを使っている場合

ロガーのラッパーメソッドに `[HideInCallstack]` を付けると、そのフレームを飛ばして呼び出し元をログの発生位置として表示する。

```csharp
using UnityEngine;

public static class MyLog
{
    [HideInCallstack]
    public static void Info(string message) => Debug.Log(message);
}
```


## インストール

`Window > Package Management > Package Manager` の `+` → `Install package from git URL...` に以下を入力する。

```
https://github.com/Atori708/miniConsole.git?path=Packages/com.atori708.miniconsole
```

## ライセンス

[MIT](Packages/com.atori708.miniconsole/LICENSE.md)
