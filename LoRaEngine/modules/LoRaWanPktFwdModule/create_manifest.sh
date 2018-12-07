#!/bin/bash
docker manifest create fbeltrao/lorawanpktfwdmodule:0.2 fbeltrao/lorawanpktfwdmodule:0.2-arm32v7 fbeltrao/lorawanpktfwdmodule:0.2-amd64
docker manifest push fbeltrao/lorawanpktfwdmodule:0.2