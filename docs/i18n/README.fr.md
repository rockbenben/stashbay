# StashBay

> Une étagère légère pour transférer vos fichiers d'une fenêtre à l'autre : déposez-les, glissez-les vers la destination, et voilà — plus besoin d'aligner deux fenêtres côte à côte.

[简体中文](../../README.md) · [English](../../README.en.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Español](README.es.md) · **Français** · [Deutsch](README.de.md) · [Português](README.pt.md) · [Русский](README.ru.md) · [Italiano](README.it.md) · [العربية](README.ar.md) · [हिन्दी](README.hi.md) · [বাংলা](README.bn.md) · [ไทย](README.th.md) · [Türkçe](README.tr.md) · [Tiếng Việt](README.vi.md) · [Bahasa Indonesia](README.id.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #048](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ Télécharger la dernière version](https://github.com/rockbenben/stashbay/releases/latest)

![Étagère compacte StashBay : une petite zone flottante toujours visible pour déposer et réutiliser vos fichiers](../../assets/shelf.png)

Une station de transit de fichiers locale et ultra-légère pour le bureau Windows. Entièrement locale, sans téléversement, sans connexion réseau requise et sans inscription. Elle s'utilise à la fois comme une étagère éphémère que l'on referme dès l'usage terminé, ou comme un établi de fichiers complet avec détection Git, instantanés de sauvegarde et annulation en un clic.

---

## Spécifications prises en charge

| Aspect | Spécification |
| :--- | :--- |
| **Système d'exploitation** | Windows 10 (1809+) / Windows 11 (support natif x64 et ARM64) |
| **Dépendances** | Les versions installable et portable officielles intègrent le runtime ; les versions légères nécessitent le .NET 10 Desktop Runtime |
| **Éléments acceptés** | Fichiers de tout format et dossiers hiérarchiques (structure relative préservée) |
| **Sources d'entrée** | Sélection de l'Explorateur Windows, liste de fichiers du presse-papiers, secouement de la souris |
| **Cibles de réécriture** | Disques locaux, partages réseau (UNC / lecteurs réseau connectés), dépôts Git |
| **Fichiers volumineux** | Prise en charge des fichiers uniques de plus de 2 Go et des chemins longs Windows (> 260 caractères) |
| **Permissions** | Exécution avec privilèges utilisateur standard par défaut (installé dans `%LOCALAPPDATA%\Programs`) ; démarrage administrateur optionnel |
| **Emplacement des données** | Sauvegardé par défaut dans `%LOCALAPPDATA%\StashBay\` (compatible avec l'ancien dossier `FileStash`) |

---

## Démarrage rapide

1. **Téléchargement** : Récupérez l'installeur `StashBay-vx.y.z-setup.exe` (aucun droit administrateur requis) ou la version portable `StashBay-vx.y.z-win-x64.zip` depuis les [Releases](https://github.com/rockbenben/stashbay/releases/latest).
2. **Afficher l'étagère** : Appuyez sur le raccourci <kbd>Win+Alt+S</kbd> pour faire apparaître la mini-étagère immédiatement près de votre curseur sur l'écran actif.
3. **Déposer des fichiers** : Sélectionnez vos fichiers dans l'Explorateur et appuyez de nouveau sur <kbd>Win+Alt+S</kbd> (ou glissez-les sur l'étagère, ou secouez la souris) : les fichiers y sont aussitôt déposés.
4. **Glisser d'un coup** : Attrapez la poignée `[⤹ Total X éléments]` au bas de l'étagère pour glisser l'ensemble des fichiers en un seul geste vers une fenêtre cible ou une application. Une fois vidée, l'étagère disparaît automatiquement en fondu.

---

## Fonctionnalités clés

### Étagère de transit minimaliste (expérience DropPoint)
* **Export groupé en un geste** : Faites glisser la poignée du compteur pour transférer tous les fichiers déposés en une seule fois vers l'Explorateur, vos applications de messagerie ou un formulaire web.
* **Fermeture automatique une fois vide** : Dès que les fichiers ont été déposés vers leur destination, ils quittent l'étagère ; lorsqu'elle est vide, la fenêtre s'estompe sans intervention manuelle.
* **Ouverture au niveau du curseur** : Le raccourci global détecte l'écran sur lequel se trouve votre souris et ouvre l'étagère à cet emplacement exact.
* **Secouer la souris pour déposer** : Lors du glissement de fichiers dans l'Explorateur, secouez brièvement la souris pour faire apparaître l'étagère et y stocker la sélection.
* **Bouton flottant magnétique** : Un widget discret au bord de l'écran qui s'ouvre d'un clic ou d'un léger glissement et s'aimante automatiquement aux bords de l'écran.
* **Multiples étagères indépendantes** : Double-cliquez sur l'icône de la zone de notification ou utilisez <kbd>Ctrl+Shift+N</kbd> pour ouvrir une nouvelle étagère temporaire ; les étagères vides sont nettoyées automatiquement.

### Sauvegarde des branches Git et réécriture sécurisée
* **Prise en compte des dépôts** : Les fichiers d'un dépôt Git conservent leur racine ; lors d'un changement de branche, une notification propose de les réécrire à leur emplacement d'origine en un clic.
* **Détection des verrous avant écriture** : Fait appel au Gestionnaire de redémarrage de Windows pour vérifier qu'aucun fichier n'est verrouillé (par Office ou un IDE) avant d'écrire.
* **Instantané de sécurité avant écrasement** : Sauvegarde préalable automatique dans un magasin Blob local avec annulation intégrale en un clic.
* **Comparaison par hachage** : Ignore l'écriture lorsque le contenu est rigoureusement identique, préservant ainsi la date de modification d'origine et évitant des recompilations inutiles.

### Établi complet
* **Quatre modes d'affichage** : Arborescence (hiérarchique), liste plate, groupé par dossier source et grandes icônes, avec mémorisation de l'état.
* **Modes de réécriture flexibles** : Emplacement d'origine, dossier cible spécifique ou nouvelle racine ; structure relative complète, nom seul ou aplatie.
* **Aperçu préventif des conflits** : Examinez en détail les actions planifiées avant confirmation : Demander, Écraser, Ignorer ou Renommer automatiquement.
* **Historique des opérations** : Journal complet pour chaque lot appliqué ; en cas d'erreur partielle, vous pouvez relancer uniquement les éléments en échec.

### 18 langues natives et thèmes
* 18 langues prises en charge nativement (parité de clés à 100 % validée par tests automatisés) et prise en charge complète du mode miroir RTL pour l'arabe (ar-SA).
* Parfaitement harmonisé avec les thèmes sombre et clair de Windows grâce à une palette graphite neutre.

---

## Raccourcis clavier fréquents

| Raccourci | Portée | Description |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Global | Affiche la mini-étagère ; si des fichiers sont sélectionnés dans l'Explorateur, les dépose aussitôt |
| <kbd>Win+Alt+W</kbd> | Global | Ouvre l'établi complet |
| <kbd>Win+Alt+V</kbd> | Global | Dépose directement les fichiers présents dans le presse-papiers |
| <kbd>Ctrl+V</kbd> | Dans la fenêtre | Colle les fichiers du presse-papiers dans l'étagère active |
| <kbd>Ctrl+Shift+N</kbd> | Dans la fenêtre | Crée une nouvelle étagère temporaire vide |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | Dans la fenêtre | Bascule entre la mini-étagère et l'établi complet |
| <kbd>Esc</kbd> | Dans la fenêtre | Dans l'établi, replie vers l'étagère ; dans l'étagère, masque dans la zone de notification |
| <kbd>Delete</kbd> | Liste | Retire les éléments sélectionnés de l'étagère |

> [!TIP]
> Tous les raccourcis globaux peuvent être modifiés ou désactivés dans les paramètres. En cas de conflit avec une autre application, StashBay affiche un avertissement clair au lieu d'échouer silencieusement.

---

## Interface en ligne de commande (CLI)

`StashBay.exe` propose plusieurs options en ligne de commande pour s'intégrer à des lanceurs externes, scripts ou flux automatisés :

| Option | Description |
| :--- | :--- |
| `--add <chemins...>` | Ajoute un ou plusieurs fichiers/dossiers à l'étagère |
| `--group <nom>` | Utilisé avec `--add` pour spécifier l'étagère cible (créée si inexistante) |
| `--apply-here <dossier>` | Écrit le contenu de l'étagère dans le dossier indiqué |
| `--apply-original` | Restaure les fichiers à leur emplacement relatif d'origine (idéal pour Git) |
| `--show` | Affiche la fenêtre principale au premier plan |
| `--minimized` | Démarre réduit dans la zone de notification (pour le démarrage automatique) |
| `--quit` | Ferme proprement l'instance en cours d'exécution |
| `--unregister-shell` | Supprime les entrées du menu contextuel, le raccourci Envoyer vers et le démarrage automatique |

---

## Limitations connues

* **Conçu exclusivement pour Windows** : Profondément intégré au Shell Windows, à la composition DWM et au contrôle d'accès ; aucune version multiplateforme n'est prévue.
* **Instantanés de contenu plutôt que liens** : Les fichiers déposés sont immédiatement copiés sous forme d'instantanés ; modifier les fichiers sources ultérieurement n'altère pas l'étagère.
* **Instance unique** : Relancer l'application transmet automatiquement les paramètres au processus principal de la zone de notification, évitant ainsi les processus orphelins.
* **Démarrage administrateur** : S'exécute par défaut avec des droits standard. Si vous devez glisser des fichiers vers des fenêtres avec privilèges élevés, activez le démarrage administrateur dans les paramètres et approuvez l'élévation UAC. [Guide de configuration du démarrage](../admin-startup.md).

---

## À propos du 365 Open Source Plan

Projet **#048** du [365 Open Source Plan](https://github.com/rockbenben/365opensource) — une personne + l'IA, plus de 300 projets open source en un an.

[Proposez votre idée →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
