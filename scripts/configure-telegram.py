import getpass
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.error
import urllib.request


def main():
    root = Path(__file__).resolve().parent.parent
    if not (root / ".env").exists():
        raise RuntimeError("Run this tool in an existing MediaHub deployment with .env.")
    token = getpass.getpass("Telegram bot token (hidden): ").strip()
    if not re.fullmatch(r"\d+:[A-Za-z0-9_-]+", token):
        raise RuntimeError("Invalid bot token format.")

    def call(method, payload):
        request = urllib.request.Request(
            f"https://api.telegram.org/bot{token}/{method}",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=35) as response:
                result = json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError(f"Telegram refused the request (HTTP {error.code}).") from None
        except urllib.error.URLError:
            raise RuntimeError("Telegram is unreachable.") from None
        if not result.get("ok"):
            raise RuntimeError("Telegram refused the request.")
        return result["result"]

    bot = call("getMe", {})
    print(f"Bot verified: @{bot['username']}")
    input("Send /start to this bot from your phone, then press Enter here: ")
    updates = call("getUpdates", {"timeout": 0, "allowed_updates": ["message", "callback_query"]})
    chats = {}
    for update in updates:
        message = update.get("message", {})
        chat = message.get("chat", {})
        sender = message.get("from", {})
        if chat.get("type") == "private" and chat.get("id") == sender.get("id") and chat.get("id", 0) > 0:
            chats[chat["id"]] = sender.get("username", sender.get("first_name", "private user"))
    if not chats:
        raise RuntimeError("No private message found. Send /start and run this tool again. Use a dedicated bot.")
    for chat_id, name in chats.items():
        print(f"Private chat {chat_id}: {name}")
    chat_id = int(input("Enter YOUR private chat ID from the list: ").strip())
    if chat_id not in chats:
        raise RuntimeError("Choose a private chat verified above.")
    call("sendMessage", {"chat_id": chat_id, "text": "MediaHub : connexion Telegram verifiee."})
    if input("Did YOU receive the message on your phone? Type YES: ").strip() != "YES":
        raise RuntimeError("Configuration cancelled.")
    updates_offset = max((update["update_id"] + 1 for update in updates), default=0)
    env_path = root / ".env"
    lines = env_path.read_text(encoding="utf-8").splitlines()
    values = {"TELEGRAM_BOT_TOKEN": token, "TELEGRAM_CHAT_ID": str(chat_id)}
    lines = [line for line in lines if line.split("=", 1)[0].strip() not in values]
    lines.extend(f"{key}={value}" for key, value in values.items())
    descriptor, temporary = tempfile.mkstemp(prefix=".env-telegram-", dir=root)
    try:
        os.chmod(temporary, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            stream.write("\n".join(lines) + "\n")
        os.replace(temporary, env_path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    subprocess.run(["docker", "compose", "exec", "-T", "db", "psql", "-U", "mediahub", "-d", "mediahub", "-c",
                    f'UPDATE "WatchSettings" SET "TelegramOffset" = {updates_offset} WHERE "Id" = 1;'], cwd=root, check=True)
    subprocess.run(["docker", "compose", "--profile", "pipeline", "up", "-d", "--force-recreate", "--wait", "api", "web", "worker"], cwd=root, check=True)
    print("Telegram configured. Only your private account can approve or blacklist releases.")


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Configuration failed: {error}")
        raise SystemExit(1)