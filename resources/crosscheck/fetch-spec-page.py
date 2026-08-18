import re, html, sys, urllib.request

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"

def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "text/html,*/*"})
    return urllib.request.urlopen(req, timeout=40).read().decode("utf-8", "replace")

def totext(d):
    d = re.sub(r'(?is)<(script|style|head).*?</\1>', ' ', d)
    d = re.sub(r'(?i)<br\s*/?>', '\n', d)
    d = re.sub(r'(?i)</(p|div|tr|h\d|li|table|pre)>', '\n', d)
    d = re.sub(r'(?i)</t[dh]>', ' | ', d)
    d = re.sub(r'<[^>]+>', '', d)
    d = html.unescape(d)
    d = re.sub(r'[ \t\xa0]+', ' ', d)
    d = re.sub(r'\n[ ]+', '\n', d)
    d = re.sub(r'\n{2,}', '\n', d)
    return d.strip()

if __name__ == "__main__":
    for u in sys.argv[1:]:
        print("=" * 20, u)
        try:
            print(totext(get(u)))
        except Exception as e:
            print("ERROR", e)
