**GoodbyeDPI-Kurulum.exe** dosyasını indirip çalıştır, kurulum bitince açılan panelde **Başlat**'a bas.

**1.1.1'de yeni:** Güncelleme doğrulama (SHA-256) ve güvenlik sertleştirmeleri eklendi; güncelleme sırasında koruma açıksa kurulum sonrasında kendiliğinden devam ediyor. Arka plandaki kaynak kullanımı azaltıldı, yöntem araması iptal edilebilir hâle getirildi ve bildirim alanı menüsüne **Günlüğü aç** eklendi. DoH motorunda önbellek TTL yaşlandırma, büyük DNS cevaplarında UDP kesme (`TC`) ve ağ değişiminde yerel DNS soneklerini yenileme eklendi.

- Şifreli DNS (DNS-over-HTTPS) içinde: ayrı bir DNSCrypt ya da DNS programı gerekmez.
- Yöntem otomatik bulunur: panel yöntemleri sırayla dener, Discord ve Roblox'u açanı seçer.
- Pencereyi kapatınca bildirim alanında kalır; koruma arka planda sürer.
- Kaldırmak için: Ayarlar > Uygulamalar > GoodbyeDPI, ya da paneldeki **Kaldır**.

Windows "Windows kişisel bilgisayarınızı korudu" uyarısı gösterirse: **Ek bilgi > Yine de çalıştır**. Dosya imzasız olduğu için bu uyarı çıkar. Bazı antivirüsler WinDivert sürücüsünü yanlışlıkla işaretleyebilir.

Gereksinim: 64 bit Windows 10 ya da 11.

`GoodbyeDPI-Panel.exe` kurulumsuz tek dosya sürümüdür: çalıştırınca kendini kurar.
