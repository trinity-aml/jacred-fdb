#!/usr/bin/bash
DEST="/home/jacred"

curl -s "http://127.0.0.1:9117/jsondb/save"

systemctl stop jacred

cd $DEST
wget https://github.com/trinity-aml/jacred-fdb/releases/latest/download/publish.zip
unzip -o publish.zip
rm -f publish.zip

systemctl start jacred
