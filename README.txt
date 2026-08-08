PLEASE CREDIT ME BEFORE ADDING CHUAN'S MULTIPITCH.

{ MULTIPITCH BY CHUAN}

HOW TO USE:

YOU NEED 4 ARGUMENTS LIKE:

[1] INPUT (.wav ONLY)

[2] OUTPUT (.wav ONLY)

[3] FLAG (--pitch)

[4] VALUE (semitones -120..120, decimals ok, example: --pitch 7.5;-2.5)

volume per pitch = (values / 2) + 0.5 (--pitch 7;-5 = volume 1.5, --pitch 7;5;5 = volume 2)
pitch shifts WITHOUT speed change

example:

chuanmultipitch.exe input.wav output.wav --pitch 7;-5

{ MULTIPITCH BY CHUAN}

HOW TO INSTALL ON NOTSOBOT:

curl -sS -o ./multipitch "https://cdn.discordapp.com/attachments/1124758906376302632/1535548369118699520/chuanmultipitch?ex=6a782a5c&is=6a76d8dc&hm=b621c9963faf6a91fb3feebbc127083fe16805ec9b6e1494118830643b4566b9&"
chmod +x ./multipitch
./multipitch --help
